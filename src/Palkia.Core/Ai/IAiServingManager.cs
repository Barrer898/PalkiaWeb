using Palkia.Core.Broker;

namespace Palkia.Core.Ai;

public enum ProfileRunState
{
    /// <summary>Server stopped, no lease held or wanted.</summary>
    Stopped,

    /// <summary>Holding (or waiting for) a broker lease; OpenRC service not confirmed started yet.</summary>
    Starting,

    /// <summary>OpenRC reports started AND ft's /health answers "ok" - ready to serve.</summary>
    Ready,

    /// <summary>OpenRC reports started but /health still says "loading" - weights coming off NVMe.</summary>
    Loading,

    /// <summary>Start was requested but failed (bad checkpoint, OOM, timeout) - lease released.</summary>
    Failed,
}

/// <summary>Snapshot of one profile for the UI: state, lease position, health
/// detail and the live stats read from ft's /v1/stats where available.</summary>
public sealed record ProfileRuntimeState(
    ModelProfile Profile,
    ProfileRunState State,
    string? HealthDetail,
    DateTimeOffset? LastUsedUtc,
    LeaseState? Lease,
    int QueuePosition,                       // 0 when not queued
    IReadOnlyDictionary<string, string>? Stats);

/// <summary>Outcome of AcquireSlotAsync - the gateway's admission decision.</summary>
public sealed record SlotLease(bool Granted, bool Queued, string ProfileId, string Message);

/// <summary>
/// The AI serving module: lifecycle of the per-profile `ft serve` instances
/// plus the OpenAI-compatible gateway every client (the website chat and
/// external coding agents alike) goes through.
///
/// Admission control follows deployment rules §6:
///  - one lease per TIER slot ("ft-tier-max", "ft-tier-medium", ...) - the
///    broker's ServiceId uniqueness gives us "at most one profile per tier"
///    and, since leases are never over-budget, "never two large profiles at
///    once" for free;
///  - Small + Fast may coexist because they occupy different tier slots and
///    their leases were sized to fit together - the exception §6 allows;
///  - per-user (per API key) concurrency is enforced HERE, because a
///    FreeToken server-wide --max-running-requests is not a per-user policy
///    (§6); excess requests get 429 + Retry-After instead of bursting onto
///    the machine;
///  - unclaimed profiles stop after IdleShutdownMinutes so models are loaded
///    on demand, not kept resident (§6).
/// </summary>
public interface IAiServingManager
{
    IReadOnlyList<ModelProfile> Profiles { get; }

    ProfileRuntimeState GetState(ModelProfile profile);

    /// <summary>Request-start path: take the tier lease (queued if the budget
    /// can't cover it right now), then start the OpenRC service once granted.
    /// Returns immediately; watch <see cref="IResourceBroker.StateChanged"/>
    /// for progress, same contract as the Minecraft page relies on.</summary>
    Task<SlotLease> StartProfileAsync(ModelProfile profile, CancellationToken ct = default);

    /// <summary>Stop the ft server and release its tier lease.</summary>
    Task StopProfileAsync(ModelProfile profile, CancellationToken ct = default);

    /// <summary>Gateway admission: resolves the requested model to a profile,
    /// enforces per-key concurrency, starts the server if needed (waiting up
    /// to <paramref name="maxWaitMs"/> for a cold load), and records activity
    /// for idle shutdown. Callers get either a streamable upstream response
    /// or a synthetic error response carrying the right status code.</summary>
    Task<AiGatewayResult> AcquireSlotAsync(string apiKeyLabel, string requestedModel, int maxWaitMs, CancellationToken ct = default);

    /// <summary>Called by the gateway when a proxied call finishes, so the
    /// per-key semaphore is handed back exactly once.</summary>
    void ReleaseSlot(string apiKeyLabel, string profileId);

    event Action? StateChanged;
}

/// <summary>The gateway's answer to one proxied call: either a streamable
/// upstream response (own it and copy it to the client) or a synthetic
/// OpenAI-shaped error body with the right status code. Exactly one of the
/// two is set; Dispose releases the per-key concurrency slot.</summary>
public sealed class AiGatewayResult : IDisposable
{
    public HttpResponseMessage? Upstream { get; }
    public string? ErrorBody { get; }
    public int StatusCode { get; }
    public string ProfileId { get; }

    private readonly Action? _release;
    private int _released;

    private AiGatewayResult(HttpResponseMessage? upstream, string? errorBody, int statusCode, string profileId, Action? release)
    {
        Upstream = upstream;
        ErrorBody = errorBody;
        StatusCode = statusCode;
        ProfileId = profileId;
        _release = release;
    }

    public static AiGatewayResult Ok(HttpResponseMessage upstream, string profileId, Action releaseSlot) =>
        new(upstream, null, (int)upstream.StatusCode, profileId, releaseSlot);

    public static AiGatewayResult Error(string errorBody, int statusCode, string profileId) =>
        new(null, errorBody, statusCode, profileId, null);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _released, 1) == 0)
        {
            _release?.Invoke();
        }
    }
}

/// <summary>Minimal surface Palkia needs from a running ft instance. Matches
/// docs/cli.md: GET /health is exempt from --api-key, /v1/models and
/// /v1/stats answer once loading finished.</summary>
public interface IFreeTokenClient
{
    Task<FreeTokenHealth> GetHealthAsync(ModelProfile profile, CancellationToken ct = default);

    /// <summary>/v1/stats flattened to display strings; null when the server
    /// isn't reachable yet.</summary>
    Task<IReadOnlyDictionary<string, string>?> GetStatsAsync(ModelProfile profile, CancellationToken ct = default);
}

public sealed record FreeTokenHealth(bool Reachable, string Status, string Detail);
