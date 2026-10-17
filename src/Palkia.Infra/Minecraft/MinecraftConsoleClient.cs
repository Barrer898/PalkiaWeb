using System.Text;
using Microsoft.Extensions.Logging;
using Palkia.Core.Minecraft;

namespace Palkia.Infra.Minecraft;

/// <summary>
/// How this actually works, since it's not obvious from the interface alone:
///
/// The server's stdin is a named pipe (mkfifo). supervise-daemon's own
/// --stdin flag turned out to be unusable for this: it opens the pipe
/// read-only before starting java (so startup blocks until something writes),
/// and java sees end-of-file as soon as the first writer closes (so only the
/// first command ever works). deploy/palkia-minecraft-launch.sh instead opens
/// the pipe read-WRITE as java's stdin, so java holds its own write end and
/// never sees EOF, and nothing blocks at startup. Anything this class writes
/// to that path arrives as if typed at an attached terminal.
///
/// Reading output is much simpler - just tail the log file supervise-daemon
/// is already writing stdout to, tracking a byte offset between polls
/// rather than re-reading the whole file every time.
/// </summary>
public sealed class MinecraftConsoleClient : IMinecraftConsoleClient
{
    // How much of the end of the log IsServerReadyAsync scans for startup/
    // shutdown markers. Big enough to cover a normal session's startup, small
    // enough to be a trivial read.
    private const int ReadyScanWindowBytes = 256 * 1024;

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

        if (!await IsServerReadyAsync(ct))
        {
            return new ConsoleCommandResult(false,
                "The server isn't ready for commands yet (still starting, shutting down, or not running). " +
                "Wait until the log shows 'Done (...)! For help, type \"help\"'.");
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

    public async Task<bool> IsServerReadyAsync(CancellationToken ct = default)
    {
        if (!File.Exists(_options.LogPath))
        {
            return false;
        }

        await using var stream = new FileStream(
            _options.LogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

        stream.Seek(Math.Max(0, stream.Length - ReadyScanWindowBytes), SeekOrigin.Begin);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false);

        // The log is appended to across restarts, so only the LAST marker in
        // the window matters: a startup line means "not ready", the "Done"
        // line means ready, a shutdown line means not ready again.
        bool? ready = null;
        string? line;
        while ((line = await reader.ReadLineAsync(ct)) is not null)
        {
            if (line.Contains("Starting org.bukkit.craftbukkit.Main", StringComparison.Ordinal) ||
                line.Contains("Stopping server", StringComparison.Ordinal) ||
                line.Contains("Stopping the server", StringComparison.Ordinal))
            {
                ready = false;
            }
            else if (line.Contains("Done (", StringComparison.Ordinal) &&
                     line.Contains("For help, type", StringComparison.Ordinal))
            {
                ready = true;
            }
        }

        // No marker in the window at all means a long-running session whose
        // startup scrolled out of it - treat that as ready rather than
        // blocking commands forever.
        return ready ?? true;
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
