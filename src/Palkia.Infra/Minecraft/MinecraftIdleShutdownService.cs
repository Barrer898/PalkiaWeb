using System.Text.RegularExpressions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Palkia.Core.Broker;
using Palkia.Core.Minecraft;
using Palkia.Core.Services;

namespace Palkia.Infra.Minecraft;

/// <summary>
/// Stops Minecraft after <see cref="MinecraftOptions.IdleShutdownMinutes"/>
/// minutes with nobody online, and releases its broker lease so the CPU/RAM
/// is free for something else.
///
/// How it knows who's online, and why it double-checks:
///
/// The cheap, quiet signal is the log - "X joined the game" / "X left the
/// game" lines, tracked incrementally (only new bytes are read each cycle)
/// and reset at every "Starting org.bukkit.craftbukkit.Main" line, so a
/// restart can't leave stale players behind. But a log-derived count can be
/// wrong (a join that scrolled out of the startup window, a changed message
/// format), and the cost of being wrong is kicking people off a server they
/// are playing on. So once the log says "empty" for the full timeout, it asks
/// the server directly with 'list' and only shuts down if the server itself
/// reports 0 players. If that answer can't be obtained or parsed, it does
/// NOT shut down - it tries again after another full timeout.
///
/// It stops the server through the OpenRC service (not by sending 'stop' down
/// the console pipe): supervise-daemon would simply respawn a java process
/// that exited on its own.
/// </summary>
public sealed class MinecraftIdleShutdownService : BackgroundService
{
    private const string MinecraftServiceId = "minecraft";

    // How far back (in approximate lines) to look when first picking up the
    // player list, e.g. right after the app itself restarts mid-session.
    private const int BootstrapLines = 10_000;

    private static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan ListReplyTimeout = TimeSpan.FromSeconds(5);

    private static readonly Regex JoinedRx = new(@"\]: (\S+) joined the game\s*$", RegexOptions.Compiled);
    private static readonly Regex LeftRx = new(@"\]: (\S+) left the game\s*$", RegexOptions.Compiled);
    private static readonly Regex ListRx = new(@"There are (\d+) of a max of (\d+) players online", RegexOptions.Compiled);

    private readonly IResourceBroker _broker;
    private readonly IServiceControlClient _serviceControl;
    private readonly IMinecraftConsoleClient _console;
    private readonly MinecraftOptions _options;
    private readonly ILogger<MinecraftIdleShutdownService> _logger;

    private readonly HashSet<string> _players = new(StringComparer.Ordinal);
    private long _logPosition = -1;
    private DateTimeOffset? _emptySince;

    public MinecraftIdleShutdownService(
        IResourceBroker broker,
        IServiceControlClient serviceControl,
        IMinecraftConsoleClient console,
        MinecraftOptions options,
        ILogger<MinecraftIdleShutdownService> logger)
    {
        _broker = broker;
        _serviceControl = serviceControl;
        _console = console;
        _options = options;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_options.IdleShutdownMinutes <= 0)
        {
            _logger.LogInformation("Minecraft idle auto-shutdown is disabled (Minecraft:IdleShutdownMinutes <= 0).");
            return;
        }

        var idleTimeout = TimeSpan.FromMinutes(_options.IdleShutdownMinutes);
        _logger.LogInformation(
            "Minecraft idle auto-shutdown enabled: stops after {Minutes} minutes with nobody online.",
            _options.IdleShutdownMinutes);

        using var timer = new PeriodicTimer(CheckInterval);
        do
        {
            try
            {
                await CheckOnceAsync(idleTimeout, stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Minecraft idle-shutdown check failed - will retry next cycle.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task CheckOnceAsync(TimeSpan idleTimeout, CancellationToken ct)
    {
        // Keep the player set current every cycle, even when we end up not
        // acting on it, so it's right whenever we do need it.
        await ConsumeLogAsync(ct);

        var lease = _broker.ActiveLeases.FirstOrDefault(l => l.Request.ServiceId == MinecraftServiceId);
        if (lease is null || !await _console.IsServerReadyAsync(ct))
        {
            // Not running (or still starting/stopping): nothing to time.
            _emptySince = null;
            return;
        }

        if (_players.Count > 0)
        {
            _emptySince = null;
            return;
        }

        var now = DateTimeOffset.UtcNow;
        _emptySince ??= now;
        if (now - _emptySince.Value < idleTimeout)
        {
            return;
        }

        // The log says nobody has been on for the whole timeout. Confirm with
        // the server itself before doing anything irreversible-ish.
        var online = await QueryOnlinePlayersAsync(ct);
        if (online != 0)
        {
            _logger.LogInformation(
                online is null
                    ? "Idle timeout reached but the server didn't answer 'list' - not shutting down, will re-check later."
                    : "Idle timeout reached by the log, but the server reports {Online} player(s) online - not shutting down.",
                online);

            // Back off a full timeout before asking again, instead of sending
            // 'list' every minute.
            _emptySince = now;
            return;
        }

        var minecraft = ManagedServiceCatalog.Find(MinecraftServiceId)!;
        _logger.LogInformation(
            "Nobody has been on Minecraft for {Minutes} minutes - stopping it.", _options.IdleShutdownMinutes);

        var result = await _serviceControl.StopAsync(minecraft, ct);
        if (!result.Succeeded)
        {
            _logger.LogWarning("Idle auto-shutdown failed to stop Minecraft: {Message}", result.Message);
            _emptySince = now;
            return;
        }

        _broker.ReleaseLease(lease.Id);
        _emptySince = null;
        _players.Clear();
    }

    private async Task ConsumeLogAsync(CancellationToken ct)
    {
        if (_logPosition < 0)
        {
            _logPosition = _console.GetInitialTailPosition(BootstrapLines);
        }

        var result = await _console.ReadNewLinesAsync(_logPosition, ct);
        _logPosition = result.NextPosition;

        foreach (var line in result.NewLines)
        {
            ApplyLogLine(line);
        }
    }

    private void ApplyLogLine(string line)
    {
        if (line.Contains("Starting org.bukkit.craftbukkit.Main", StringComparison.Ordinal))
        {
            _players.Clear();
            return;
        }

        var joined = JoinedRx.Match(line);
        if (joined.Success)
        {
            _players.Add(joined.Groups[1].Value);
            return;
        }

        var left = LeftRx.Match(line);
        if (left.Success)
        {
            _players.Remove(left.Groups[1].Value);
        }
    }

    /// <summary>Sends 'list' and waits for the reply line in the log. Null if
    /// the command couldn't be sent or no parseable reply showed up in time.</summary>
    private async Task<int?> QueryOnlinePlayersAsync(CancellationToken ct)
    {
        // 0 lines from the end = the current end of the log, so we only look
        // at output produced after we ask.
        var position = _console.GetInitialTailPosition(0);

        var sent = await _console.SendCommandAsync("list", ct);
        if (!sent.Succeeded)
        {
            _logger.LogWarning("Couldn't send 'list' to confirm the player count: {Message}", sent.Message);
            return null;
        }

        var deadline = DateTimeOffset.UtcNow + ListReplyTimeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(500), ct);

            var result = await _console.ReadNewLinesAsync(position, ct);
            position = result.NextPosition;

            foreach (var line in result.NewLines)
            {
                var match = ListRx.Match(line);
                if (match.Success && int.TryParse(match.Groups[1].Value, out var count))
                {
                    return count;
                }
            }
        }

        return null;
    }
}
