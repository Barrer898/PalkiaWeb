using System.Text;
using Microsoft.Extensions.Logging;
using Palkia.Core.Minecraft;

namespace Palkia.Infra.Minecraft;

/// <summary>
/// How this actually works, since it's not obvious from the interface alone:
///
/// OpenRC's supervise-daemon normally only lets you redirect the child
/// process's stdout/stderr (output_log/error_log) - there's no equivalent
/// for feeding it input, which is exactly what "console access" needs. The
/// standard trick (see deploy/palkia-minecraft.initd) is a named pipe
/// (mkfifo) passed via supervise-daemon's --stdin flag: the kernel treats
/// it exactly like a regular file for I/O purposes, so the java process
/// reads from it as its console input, and anything this class writes to
/// that same path arrives as if typed at an attached terminal.
///
/// Reading output is much simpler - just tail the log file supervise-daemon
/// is already writing stdout to, tracking a byte offset between polls
/// rather than re-reading the whole file every time.
/// </summary>
public sealed class MinecraftConsoleClient : IMinecraftConsoleClient
{
    private readonly MinecraftConsoleOptions _options;
    private readonly ILogger<MinecraftConsoleClient> _logger;

    public MinecraftConsoleClient(MinecraftConsoleOptions options, ILogger<MinecraftConsoleClient> logger)
    {
        _options = options;
        _logger = logger;
    }

    public async Task<ConsoleCommandResult> SendCommandAsync(string command, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return new ConsoleCommandResult(false, "Empty command.");
        }

        if (!File.Exists(_options.ConsoleFifoPath))
        {
            return new ConsoleCommandResult(false,
                $"Console pipe not found at '{_options.ConsoleFifoPath}'. It only exists while the " +
                "OpenRC service is configured with --stdin and has been started at least once - see " +
                "deploy/README.md if this keeps happening.");
        }

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(_options.CommandWriteTimeoutSeconds));

            // Opening a FIFO for writing blocks until some process has it
            // open for reading. If Minecraft isn't actually running, nothing
            // ever will, so without the timeout this would hang forever.
            await using var pipe = new FileStream(
                _options.ConsoleFifoPath, FileMode.Open, FileAccess.Write, FileShare.None,
                bufferSize: 1, useAsync: true);

            var bytes = Encoding.UTF8.GetBytes(command.TrimEnd('\r', '\n') + "\n");
            await pipe.WriteAsync(bytes, cts.Token);
            await pipe.FlushAsync(cts.Token);

            return new ConsoleCommandResult(true, $"Sent: {command}");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new ConsoleCommandResult(false,
                "Timed out opening the console pipe - Minecraft is probably not running.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to send Minecraft console command.");
            return new ConsoleCommandResult(false, $"Failed to send command: {ex.Message}");
        }
    }

    public long GetInitialTailPosition(int approxLinesFromEnd = 150)
    {
        if (!File.Exists(_options.LogPath))
        {
            return 0;
        }

        // Deliberately approximate - seeks back a byte estimate rather than
        // actually counting lines, so a fresh console view isn't either the
        // entire history of a long-lived server or completely empty.
        const int approxBytesPerLine = 120;
        var seekBack = (long)approxLinesFromEnd * approxBytesPerLine;

        var length = new FileInfo(_options.LogPath).Length;
        return Math.Max(0, length - seekBack);
    }

    public async Task<LogReadResult> ReadNewLinesAsync(long fromPosition, CancellationToken ct = default)
    {
        if (!File.Exists(_options.LogPath))
        {
            return new LogReadResult(Array.Empty<string>(), 0);
        }

        await using var stream = new FileStream(
            _options.LogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

        var length = stream.Length;

        if (fromPosition > length)
        {
            // Log was rotated/truncated since the last read - restart from
            // the beginning rather than seeking past the current end.
            fromPosition = 0;
        }

        if (fromPosition >= length)
        {
            return new LogReadResult(Array.Empty<string>(), fromPosition);
        }

        stream.Seek(fromPosition, SeekOrigin.Begin);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false);

        var lines = new List<string>();
        string? line;
        while ((line = await reader.ReadLineAsync(ct)) is not null)
        {
            lines.Add(line);
        }

        // Deliberately 'length' (captured before reading) rather than
        // stream.Position afterwards - StreamReader buffers ahead in chunks,
        // so its underlying stream position isn't reliably "end of last
        // complete line". Using the length we already read up to is exact,
        // at the minor cost of possibly re-reading a handful of trailing
        // bytes if the very last line was still being written mid-poll.
        return new LogReadResult(lines, length);
    }
}
