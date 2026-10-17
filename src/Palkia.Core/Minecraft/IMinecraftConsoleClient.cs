namespace Palkia.Core.Minecraft;

public sealed record ConsoleCommandResult(bool Succeeded, string Message);

public sealed record LogReadResult(IReadOnlyList<string> NewLines, long NextPosition);

/// <summary>
/// Talks to the running Minecraft process without going through OpenRC at
/// all - this is IPC with the java process itself (a named pipe for stdin,
/// tailing its log file for output), completely separate from
/// IServiceControlClient's job of starting/stopping the OpenRC service.
/// </summary>
public interface IMinecraftConsoleClient
{
    /// <summary>
    /// Sends one line to the server's console, as if typed at a terminal
    /// attached to it (no leading slash - "say hello", not "/say hello").
    /// </summary>
    Task<ConsoleCommandResult> SendCommandAsync(string command, CancellationToken ct = default);

    /// <summary>
    /// A reasonable starting byte offset for a fresh console view - roughly
    /// the last <paramref name="approxLinesFromEnd"/> lines, not exact.
    /// </summary>
    long GetInitialTailPosition(int approxLinesFromEnd = 150);

    /// <summary>
    /// Reads whatever's been appended to the log since <paramref name="fromPosition"/>.
    /// Pass the returned NextPosition back in on the next call to keep tailing.
    /// </summary>
    Task<LogReadResult> ReadNewLinesAsync(long fromPosition, CancellationToken ct = default);
}
