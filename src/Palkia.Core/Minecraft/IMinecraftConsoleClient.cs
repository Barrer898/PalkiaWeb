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
    /// <remarks>
    /// Refuses (returns Succeeded=false) while the server is still starting or
    /// shutting down - see <see cref="IsServerReadyAsync"/> for why.
    /// </remarks>
    Task<ConsoleCommandResult> SendCommandAsync(string command, CancellationToken ct = default);

    /// <summary>
    /// True once the server has finished starting up ("Done (...)! For help,
    /// type "help"" in the log) and hasn't begun shutting down since. Commands
    /// read from stdin during startup are turned into command sources before the
    /// world exists, and then crash with a NullPointerException when they finally
    /// run - so sending before this is true is never useful.
    /// </summary>
    Task<bool> IsServerReadyAsync(CancellationToken ct = default);

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
