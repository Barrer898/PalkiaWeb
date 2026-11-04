using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Palkia.Core.Ai;

namespace Palkia.Infra.Ai;

/// <summary>
/// Keeps AiServingManager's background loops alive for the lifetime of the
/// app, exactly like MinecraftIdleShutdownService does for Minecraft:
///  - health/stats poll (5s) feeding the AI page and gateway admission,
///  - idle auto-stop releasing tier leases (§6: on-demand, not resident),
///  - startup reconciliation adopting leases for ft servers that were
///    already running underneath an app restart,
///  - booting the configured AlwaysOnProfiles (tiny models only - see
///    FreeTokenOptions.AlwaysOnProfiles for why big ones don't belong here).
/// </summary>
public sealed class AiServingHostedService : IHostedService
{
    private readonly AiServingManager _manager;
    private readonly FreeTokenOptions _options;
    private readonly ILogger<AiServingHostedService> _logger;
    private CancellationTokenSource? _cts;
    private Task? _healthLoop;
    private Task? _idleLoop;

    public AiServingHostedService(
        AiServingManager manager,
        FreeTokenOptions options,
        ILogger<AiServingHostedService> logger)
    {
        _manager = manager;
        _options = options;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken ct)
    {
        await _manager.ReconcileAtStartupAsync(ct);

        foreach (var id in _options.AlwaysOnProfiles)
        {
            var profile = ModelProfileCatalog.Find(id);
            if (profile is null)
            {
                _logger.LogWarning("FreeToken:AlwaysOnProfiles lists unknown profile '{Id}' - ignoring.", id);
                continue;
            }

            var result = await _manager.StartProfileAsync(profile, ct);
            _logger.LogInformation("Always-on start requested for {Profile}: {Message}", profile.Id, result.Message);
        }

        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _healthLoop = Task.Run(() => RunLoopSafe(_manager.RunHealthPollLoopAsync, _cts.Token), CancellationToken.None);
        _idleLoop = Task.Run(() => RunLoopSafe(_manager.RunIdleMaintenanceAsync, _cts.Token), CancellationToken.None);
    }

    public async Task StopAsync(CancellationToken ct)
    {
        _cts?.Cancel();

        // Don't await with the host's own token - if shutdown was triggered by
        // Ctrl+C that token is already cancelled and these loops would never
        // get a chance to unwind cleanly. Give them a moment, then move on;
        // the ft services themselves keep running (their leases die with the
        // app and get re-adopted on next boot - deliberate, rules §9).
        var grace = Task.WhenAny(Task.WhenAll(NonNull(_healthLoop, _idleLoop)), Task.Delay(TimeSpan.FromSeconds(5), CancellationToken.None));
        await grace;
    }

    private static Task[] NonNull(params Task?[] tasks) => tasks.Where(t => t is not null).Cast<Task>().ToArray();

    private async Task RunLoopSafe(Func<CancellationToken, Task> loop, CancellationToken ct)
    {
        try
        {
            await loop(ct);
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
        catch (Exception ex)
        {
            // A crashed poll loop means the AI page goes stale and cold starts
            // never get noticed - say so loudly rather than dying silently.
            _logger.LogError(ex, "AI maintenance loop crashed; model state updates have stopped until restart.");
        }
    }
}
