using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Palkia.Core.Ai;
using Palkia.Core.Broker;
using Palkia.Core.Services;

namespace Palkia.Infra.Ai;

/// <summary>
/// Owns the lease/start/stop lifecycle of the per-profile `ft serve`
/// instances, mirroring how the Minecraft page drives its broker-managed
/// service - with one important difference: leases are taken on a TIER slot
/// id ("ft-tier-medium"), not a profile id. The broker already guarantees
/// "one lease per ServiceId" and "never over budget", so tier slots give the
/// deployment rules' core constraints almost for free:
///
///  - at most one profile per tier running (rules §3 five-profile lineup),
///  - never two large profiles concurrently - their leases simply don't fit
///    the shared budget together (§6),
///  - Small + Fast CAN coexist - different tiers, sized to fit together, the
///    one intended exception (§6),
///  - requests that don't fit right now queue FIFO instead of collapsing the
///    box (§6: "add a queue rather than allowing bursts").
///
/// A second layer sits on top: per-API-key in-flight semaphores, because a
/// FreeToken server-wide --max-running-requests is explicitly NOT a per-user
/// concurrency policy (§6). Over-limit requests get 429+Retry-After from the
/// gateway rather than piling onto the machine's own queue indefinitely.
/// </summary>
public sealed class AiServingManager : IAiServingManager
{
    private readonly IResourceBroker _broker;
    private readonly IServiceControlClient _serviceControl;
    private readonly IFreeTokenClient _ft;
    private readonly FreeTokenOptions _options;
    private readonly ILogger<AiServingManager> _logger;

    private readonly SemaphoreSlim _lifecycleLock = new(1, 1);
    private readonly ConcurrentDictionary<string, ProfileSession> _sessions = new();
    private readonly ConcurrentDictionary<(string Key, string Profile), InflightCounter> _perKeyInflight = new();

    public AiServingManager(
        IResourceBroker broker,
        IServiceControlClient serviceControl,
        IFreeTokenClient ft,
        FreeTokenOptions options,
        ILogger<AiServingManager> logger)
    {
        _broker = broker;
        _serviceControl = serviceControl;
        _ft = ft;
        _options = options;
        _logger = logger;

        // The catalog is the trust boundary for WHICH profiles exist; the
        // loopback shared secrets (ft's own --api-key) come from config so
        // they never live in a committed file. The launcher reads the same
        // keys.env values - if they disagree, requests 401 upstream and the
        // health poll surfaces it as Failed.
        foreach (var profile in ModelProfileCatalog.All)
        {
            if (options.UpstreamApiKeys.TryGetValue(profile.Id, out var key) && !string.IsNullOrWhiteSpace(key))
            {
                profile.UpstreamApiKey = key;
            }
        }

        _broker.StateChanged += OnBrokerStateChanged;
    }

    public IReadOnlyList<ModelProfile> Profiles => ModelProfileCatalog.All;

    public event Action? StateChanged;

    /// <summary>Lease/broker ServiceId for a profile's tier slot.</summary>
    public static string TierSlotId(ModelProfile p) => $"ft-tier-{p.Tier.ToString().ToLowerInvariant()}";

    private sealed class ProfileSession
    {
        public required ModelProfile Profile { get; init; }
        public Lease? Lease;                       // held tier-slot lease (may be Queued)
        public volatile bool StartRequested;       // OpenRC start issued for the current lease
        public volatile ProfileRunState State = ProfileRunState.Stopped;
        public string? HealthDetail;
        public DateTimeOffset LastUsedUtc = DateTimeOffset.UtcNow;
        public IReadOnlyDictionary<string, string>? Stats;
    }

    /// <summary>Per-(key,profile) in-flight count with an admission deadline,
    /// so queued-but-not-yet-admitted requests still count as busy and the
    /// idle timer can't stop a server out from under them.</summary>
    private sealed class InflightCounter
    {
        public int Count;
        public DateTimeOffset AdmissionDeadlineUtc;
    }

    public ProfileRuntimeState GetState(ModelProfile profile)
    {
        var session = _sessions.GetOrAdd(profile.Id, _ => new ProfileSession { Profile = profile });

        var leaseState = session.Lease?.State;
        var queuePos = 0;
        if (leaseState == LeaseState.Queued && session.Lease is { } q)
        {
            queuePos = _broker.QueuedLeases.ToList().FindIndex(l => l.Id == q.Id) + 1;
        }

        return new ProfileRuntimeState(
            profile, session.State, session.HealthDetail, session.LastUsedUtc, leaseState, queuePos, session.Stats);
    }

    // --- Manual start/stop (the AI page's buttons) ----------------------------

    public async Task<SlotLease> StartProfileAsync(ModelProfile profile, CancellationToken ct = default)
    {
        await _lifecycleLock.WaitAsync(ct);
        try
        {
            var session = _sessions.GetOrAdd(profile.Id, _ => new ProfileSession { Profile = profile });

            if (session.Lease is { State: LeaseState.Active or LeaseState.Queued } existing)
            {
                return new SlotLease(existing.State == LeaseState.Active, existing.State == LeaseState.Queued,
                    profile.Id, "Already running or queued.");
            }

            Lease lease;
            try
            {
                lease = _broker.RequestLease(BuildLeaseRequest(profile));
            }
            catch (LeaseRejectedException ex)
            {
                // Another profile of the same tier holds the slot, or this one
                // could never fit alongside what's already leased. Refuse
                // visibly rather than pretend to queue forever.
                session.State = ProfileRunState.Failed;
                session.HealthDetail = ex.Message;
                RaiseStateChanged();
                return new SlotLease(false, false, profile.Id, ex.Message);
            }

            session.Lease = lease;
            session.StartRequested = false;
            session.State = ProfileRunState.Starting;
            RaiseStateChanged();

            if (lease.State == LeaseState.Active)
            {
                await StartServiceForActiveLeaseAsync(session, ct);
                return new SlotLease(true, false, profile.Id, "Lease granted, starting server.");
            }

            return new SlotLease(false, true, profile.Id,
                "Queued for resources - the server starts automatically when enough budget frees up.");
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    public async Task StopProfileAsync(ModelProfile profile, CancellationToken ct = default)
    {
        await _lifecycleLock.WaitAsync(ct);
        try
        {
            if (!_sessions.TryGetValue(profile.Id, out var session))
            {
                return;
            }

            var result = await _serviceControl.StopAsync(ManagedServiceFor(profile), ct);
            if (!result.Succeeded)
            {
                _logger.LogWarning("Stopping {Profile} failed: {Message}", profile.Id, result.Message);
            }

            if (session.Lease is { } lease)
            {
                _broker.ReleaseLease(lease.Id);
            }

            session.Lease = null;
            session.StartRequested = false;
            session.State = ProfileRunState.Stopped;
            session.HealthDetail = null;
            session.Stats = null;
            RaiseStateChanged();
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    // --- Gateway admission -----------------------------------------------------

    public async Task<AiGatewayResult> AcquireSlotAsync(string apiKeyLabel, string requestedModel, int maxWaitMs, CancellationToken ct = default)
    {
        var profile = AiRequestRouter.ResolveProfile(requestedModel);
        if (profile is null)
        {
            return AiGatewayResult.Error(
                AiRequestRouter.OpenAiError("model_not_found",
                    $"'{requestedModel}' is not a profile on this Palkia box. See GET /api/llm/v1/models."),
                404, requestedModel);
        }

        var counter = _perKeyInflight.GetOrAdd((apiKeyLabel, profile.Id), _ => new InflightCounter());

        // Per-key concurrency (rules §6): refuse fast rather than let one
        // client pile an unbounded personal backlog onto the machine. The
        // deadline keeps the idle timer honest while we wait below.
        lock (counter)
        {
            if (counter.Count >= profile.MaxConcurrentRequests)
            {
                return AiGatewayResult.Error(
                    AiRequestRouter.OpenAiError("rate_limited",
                        $"Too many concurrent requests for '{profile.ApiName}' on this key (limit {profile.MaxConcurrentRequests})."),
                    429, profile.Id);
            }
            counter.Count++;
            counter.AdmissionDeadlineUtc = DateTimeOffset.UtcNow.AddMilliseconds(maxWaitMs);
        }

        var released = 0;
        void ReleaseSlot()
        {
            if (Interlocked.Exchange(ref released, 1) == 0)
            {
                lock (counter)
                {
                    counter.Count--;
                }
            }
        }

        try
        {
            var session = _sessions.GetOrAdd(profile.Id, _ => new ProfileSession { Profile = profile });
            session.LastUsedUtc = DateTimeOffset.UtcNow;

            if (session.Lease is null or { State: LeaseState.Released })
            {
                var start = await StartProfileAsync(profile, ct);
                if (!start.Granted && !start.Queued)
                {
                    ReleaseSlot();
                    return AiGatewayResult.Error(
                        AiRequestRouter.OpenAiError("admission_denied", start.Message), 409, profile.Id);
                }
            }

            // Wait for lease-grant -> service-start -> /health "ok", bounded
            // by maxWaitMs: cold NVMe loads of the big profiles take minutes
            // (§7 says loading is what NVMe IS for - paging active weights is
            // what it isn't).
            var deadline = DateTimeOffset.UtcNow.AddMilliseconds(maxWaitMs);
            while (DateTimeOffset.UtcNow < deadline)
            {
                ct.ThrowIfCancellationRequested();

                if (session.State == ProfileRunState.Failed)
                {
                    ReleaseSlot();
                    return AiGatewayResult.Error(
                        AiRequestRouter.OpenAiError("upstream_error", session.HealthDetail ?? "profile failed to start"),
                        502, profile.Id);
                }

                if (session.Lease is { State: LeaseState.Active })
                {
                    var health = await _ft.GetHealthAsync(profile, ct);
                    session.HealthDetail = health.Detail;

                    if (health.Status == "ok")
                    {
                        session.State = ProfileRunState.Ready;
                        RaiseStateChanged();
                        return AiGatewayResult.Ok(BuildReadyMarker(profile), profile.Id, ReleaseSlot);
                    }

                    session.State = health.Reachable ? ProfileRunState.Loading : ProfileRunState.Starting;
                }

                RaiseStateChanged();
                await Task.Delay(TimeSpan.FromSeconds(2), ct);
            }

            ReleaseSlot();
            return AiGatewayResult.Error(
                AiRequestRouter.OpenAiError("not_ready",
                    $"'{profile.ApiName}' is still waiting for resources or loading weights " +
                    "(see the Palkia AI page for queue position and load progress). Retry later."),
                503, profile.Id);
        }
        catch (OperationCanceledException)
        {
            ReleaseSlot();
            throw;
        }
    }

    public void ReleaseSlot(string apiKeyLabel, string profileId)
    {
        // No-op by design: release travels inside the AiGatewayResult's
        // IDisposable so a dropped response can't leak a permit. Kept on the
        // interface for callers managing slots themselves.
    }

    public string UpstreamUrlFor(ModelProfile profile) => BuildReadyMarker(profile).ReasonPhrase!;

    private HttpResponseMessage BuildReadyMarker(ModelProfile profile)
    {
        // The endpoint performs the real forwarded call itself (it owns the
        // inbound request body stream); this marker just carries the upstream
        // base URL to it via ReasonPhrase.
        return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            ReasonPhrase = $"http://{_options.BindHost}:{profile.Port}",
        };
    }

    // --- Broker integration ----------------------------------------------------

    private int _promotePumpScheduled;

    private void OnBrokerStateChanged()
    {
        // Housekeeping that must happen inline (mirrors what the Minecraft
        // page does when it notices its lease went Released).
        foreach (var session in _sessions.Values)
        {
            if (session.Lease is { State: LeaseState.Released })
            {
                session.Lease = null;
                session.StartRequested = false;
                session.State = ProfileRunState.Stopped;
            }
        }

        // Deferred starts: when a queued tier-slot lease gets promoted to
        // Active, that's our cue to start the ft service. This handler fires
        // SYNCHRONOUSLY from inside RequestLease/ReleaseLease, which already
        // hold _broker.Gate and (for our own calls) _lifecycleLock - so the
        // promotion pump hops onto the thread pool instead of touching any
        // lock here. Same reason the Minecraft page wraps its handler body
        // in InvokeAsync.
        if (Interlocked.Exchange(ref _promotePumpScheduled, 1) == 0)
        {
            _ = Task.Run(PromoteQueuedSessionsAsync);
        }

        RaiseStateChanged();
    }

    private async Task PromoteQueuedSessionsAsync()
    {
        try
        {
            await _lifecycleLock.WaitAsync();
            try
            {
                foreach (var session in _sessions.Values.ToList())
                {
                    // Re-read under the lock: the lease may have moved again
                    // between the event and this pump running.
                    var live = session.Lease is { } l ? _broker.ActiveLeases.FirstOrDefault(x => x.Id == l.Id) : null;
                    if (live is not null && !session.StartRequested)
                    {
                        await StartServiceForActiveLeaseAsync(session, CancellationToken.None);
                    }
                }
            }
            finally
            {
                _lifecycleLock.Release();
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ft lease-promotion pump failed.");
        }
        finally
        {
            Interlocked.Exchange(ref _promotePumpScheduled, 0);
        }
    }

    private async Task StartServiceForActiveLeaseAsync(ProfileSession session, CancellationToken ct)
    {
        if (session.StartRequested)
        {
            return;
        }
        session.StartRequested = true;

        var result = await _serviceControl.StartAsync(ManagedServiceFor(session.Profile), ct);
        if (!result.Succeeded)
        {
            _logger.LogWarning("ft service for {Profile} failed to start: {Message}", session.Profile.Id, result.Message);
            session.State = ProfileRunState.Failed;
            session.HealthDetail = result.Message;

            if (session.Lease is { } lease)
            {
                _broker.ReleaseLease(lease.Id);   // don't hold budget for a process that isn't running
            }
            session.Lease = null;
            session.StartRequested = false;
        }
        else
        {
            session.State = ProfileRunState.Starting;
        }

        RaiseStateChanged();
    }

    /// <summary>Reconcile against reality at app startup: an ft server left
    /// running (app restarted underneath it) must adopt its tier-slot lease
    /// so the broker's accounting matches the box - same idea as Program.cs's
    /// ReconcileBrokerAsync for Minecraft, extended to all five profiles.</summary>
    public async Task ReconcileAtStartupAsync(CancellationToken ct = default)
    {
        await _lifecycleLock.WaitAsync(ct);
        try
        {
            foreach (var profile in ModelProfileCatalog.All)
            {
                var status = await _serviceControl.GetStatusAsync(ManagedServiceFor(profile), ct);
                if (status.State != ServiceRunState.Started)
                {
                    continue;
                }

                var lease = _broker.AdoptExistingLease(BuildLeaseRequest(profile));
                var session = _sessions.GetOrAdd(profile.Id, _ => new ProfileSession { Profile = profile });
                session.Lease = lease;
                session.StartRequested = true;
                session.State = ProfileRunState.Starting;

                _logger.LogInformation(
                    "{Profile} ft server was already running at startup - adopted its {Tier} tier lease.",
                    profile.Id, profile.TierLabel);
            }
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    // --- Background maintenance (hosted service calls these) --------------------

    /// <summary>Idle auto-stop: any started profile nobody has proxied a
    /// request to for IdleShutdownMinutes gets stopped and its lease freed
    /// (rules §6: models loaded on demand, not kept resident).</summary>
    public async Task RunIdleMaintenanceAsync(CancellationToken ct)
    {
        if (_options.IdleShutdownMinutes <= 0)
        {
            _logger.LogInformation("AI idle auto-shutdown disabled (FreeToken:IdleShutdownMinutes <= 0).");
            return;
        }

        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        while (await timer.WaitForNextTickAsync(ct))
        {
            var idle = TimeSpan.FromMinutes(_options.IdleShutdownMinutes);
            foreach (var session in _sessions.Values.ToList())
            {
                if (session.Lease is not { State: LeaseState.Active })
                {
                    continue;
                }

                // Never stop a profile with traffic in flight or pending
                // admission - count every key's live requests against it.
                var inflight = _perKeyInflight
                    .Where(kv => kv.Key.Profile == session.Profile.Id)
                    .Select(kv => kv.Value)
                    .Count(c => { lock (c) { return c.Count > 0 || c.AdmissionDeadlineUtc > DateTimeOffset.UtcNow; } });

                if (inflight > 0 || DateTimeOffset.UtcNow - session.LastUsedUtc < idle)
                {
                    continue;
                }

                // Always-on profiles stay resident until stopped by hand.
                if (_options.AlwaysOnProfiles.Any(id => string.Equals(id, session.Profile.Id, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                _logger.LogInformation(
                    "{Profile} idle for {Minutes} minutes - stopping it and releasing its lease.",
                    session.Profile.Id, _options.IdleShutdownMinutes);

                await StopProfileAsync(session.Profile, ct);
            }
        }
    }

    /// <summary>Refresh /health + /v1/stats for leased profiles so the UI can
    /// show load progress and tok/s without each render paying for HTTP.</summary>
    public async Task RunHealthPollLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        while (await timer.WaitForNextTickAsync(ct))
        {
            foreach (var session in _sessions.Values)
            {
                if (session.Lease is not { State: LeaseState.Active })
                {
                    session.Stats = null;
                    continue;
                }

                var health = await _ft.GetHealthAsync(session.Profile, ct);
                session.HealthDetail = health.Detail;
                session.State = health.Status switch
                {
                    "ok" => ProfileRunState.Ready,
                    "loading" => ProfileRunState.Loading,
                    "error" => ProfileRunState.Failed,
                    _ => session.StartRequested ? ProfileRunState.Starting : ProfileRunState.Stopped,
                };

                session.Stats = health.Status == "ok" ? await _ft.GetStatsAsync(session.Profile, ct) : null;
            }

            RaiseStateChanged();
        }
    }

    // --- helpers ----------------------------------------------------------------

    private LeaseRequest BuildLeaseRequest(ModelProfile profile) => new(
        TierSlotId(profile),
        $"{profile.TierLabel}: {profile.ApiName}",
        new ResourceBudget(profile.LeaseCpuCores, profile.LeaseMemoryMb, profile.LeaseVramMb));

    private static ManagedService ManagedServiceFor(ModelProfile profile) => new(
        Id: profile.RcName,
        DisplayName: profile.DisplayName,
        RcName: profile.RcName,
        Description: profile.Description,
        AdminOnly: false,
        BrokerManaged: true);

    private void RaiseStateChanged() => StateChanged?.Invoke();
}
