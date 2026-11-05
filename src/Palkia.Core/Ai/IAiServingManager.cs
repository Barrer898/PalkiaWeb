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
/// Admission outcome for one gateway request. Exactly one path: either
/// <see cref="Profile"/> names a Ready profile to forward to (and the caller
/// MUST dispose this object when the proxied response has fully left the
/// wire - streaming included), or <see cref="ErrorBody"/> carries an
/// OpenAI-shaped error with the right status code. Disposing is idempotent
/// and also wired to ResponseCompleted/RequestAborted by the endpoint, so a
/// client disconnect mid-stream can't leak the per-key permit.
/// </summary>
public sealed class AiAdmission : IDisposable
{
    public ModelProfile? Profile { get; }
    public string? ErrorBody { get; }
    public int StatusCode { get; }

    /// <summary>Seconds for the Retry-After header on 429 refusals.</summary>
    public int RetryAfterSeconds { get; }

    private readonly IDisposable? _permit;
    private int _disposed;

    private AiAdmission(ModelProfile? profile, string? errorBody, int statusCode, int retryAfter, IDisposable? permit)
    {
        Profile = profile;
        ErrorBody = errorBody;
        StatusCode = statusCode;
        RetryAfterSeconds = retryAfter;
        _permit = permit;
    }

    public static AiAdmission Denied(string errorBody, int statusCode, string profileIdHint = "") =>
        new(null, errorBody, statusCode, 0, null);

    public static AiAdmission RateLimited(string errorBody, int retryAfterSeconds) =>
        new(null, errorBody, 429, retryAfterSeconds, null);

    public static AiAdmission Ready(ModelProfile profile, IDisposable permit) =>
        new(profile, null, 0, 0, permit);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _permit?.Dispose();
        }
    }
}

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

    /// <summary>Gateway admission, in the order PR review item 3 demands:
    /// resolve the model, check the key's AllowedProfiles, take the per-key
    /// concurrency permit, and ONLY THEN touch leases/start servers/queue.
    /// An unauthorized or over-limit key can never trigger a model startup,
    /// consume a broker queue position or hold resources.</summary>
    Task<AiAdmission> AdmitRequestAsync(AiCaller caller, string? requestedModel, int maxWaitMs, CancellationToken ct = default);

    /// <summary>Base URL ("http://127.0.0.1:&lt;port&gt;") of a profile's ft
    /// instance on loopback. The gateway endpoint owns the inbound request
    /// body stream, so it performs the actual forwarded call itself.</summary>
    string UpstreamUrlFor(ModelProfile profile);

    /// <summary>The loopback shared secret (ft's own --api-key) for a profile,
    /// if one is configured. Callers that talk to ft directly (the website
    /// chat service) must present it; client-facing keys never travel
    /// upstream. Empty when no key is set.</summary>
    string UpstreamApiKeyFor(ModelProfile profile);

    event Action? StateChanged;
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
