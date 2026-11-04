using System.Collections.Concurrent;

namespace Palkia.Core.Ai;

/// <summary>
/// Tracks which model tiers a given API key currently has in flight, so
/// <see cref="PerKeyConcurrencyPolicy"/> can be applied atomically at
/// admission time. One instance lives inside AiServingManager for the whole
/// box; it knows nothing about leases or HTTP — it just answers "may this
/// identity take a slot on this tier?" and hands back a release token.
/// </summary>
public sealed class PerKeyConcurrencyTracker
{
    private sealed class KeyState
    {
        public readonly object Gate = new();
        public readonly List<ModelTier> Active = new();
        public int PendingAdmission;   // admitted but still queueing/loading (idle-timer busy signal)
        public int InFlight;           // holders currently inside Dispose()->Release()
    }

    private readonly ConcurrentDictionary<AiKeyId, KeyState> _states = new();

    /// <summary>Tiers the identity currently holds permits on (snapshot).</summary>
    public IReadOnlyList<ModelTier> ActiveTiers(AiKeyId id)
    {
        if (!_states.TryGetValue(id, out var state))
        {
            return Array.Empty<ModelTier>();
        }

        lock (state.Gate)
        {
            return state.Active.ToList();
        }
    }

    /// <summary>True while some request for this identity is live or waiting
    /// for admission. Idle maintenance uses it as a busy signal.</summary>
    public bool IsBusy(AiKeyId id)
    {
        if (!_states.TryGetValue(id, out var state))
        {
            return false;
        }

        lock (state.Gate)
        {
            return state.Active.Count > 0 || state.PendingAdmission > 0;
        }
    }

    /// <summary>
    /// Atomic check-and-take. Returns null when the policy refuses (caller
    /// must answer 429 + Retry-After), otherwise a release token that hands
    /// the permit back exactly once — safe to call Dispose() from normal
    /// completion, streaming end, client disconnect, cancellation or an
    /// exception unwind, in any combination.
    /// </summary>
    public IDisposable? TryAcquire(AiKeyId id, ModelTier tier)
    {
        var state = _states.GetOrAdd(id, _ => new KeyState());

        lock (state.Gate)
        {
            if (!PerKeyConcurrencyPolicy.Allows(tier, state.Active))
            {
                return null;
            }

            state.Active.Add(tier);
        }

        return new Releaser(this, id, state, tier);
    }

    /// <summary>
    /// Wraps an acquire with a "pending admission" marker: the permit is held
    /// AND the identity counts as busy until the token is disposed, even if
    /// the request is still queueing behind another model's lease or waiting
    /// through a cold NVMe load. AiServingManager uses this so the idle
    /// timer can never stop a server out from under a request that has been
    /// admitted but not yet forwarded.
    /// </summary>
    public IDisposable? TryAcquireForAdmission(AiKeyId id, ModelTier tier)
    {
        var releaser = TryAcquire(id, tier);
        if (releaser is null)
        {
            return null;
        }

        var state = _states.GetOrAdd(id, _ => new KeyState());
        lock (state.Gate)
        {
            state.PendingAdmission++;
        }

        return new AdmissionToken(releaser, state);
    }

    private void Release(AiKeyId id, KeyState state, ModelTier tier)
    {
        lock (state.Gate)
        {
            state.Active.Remove(tier);

            // Prune the ledger only when the key is fully quiet (no permits,
            // nothing awaiting admission) and no other thread is mid-release
            // on this same state - see Releaser.InFlight. A racing
            // GetOrAdd may hand out the doomed state; InFlight makes that
            // harmless, worst case a stale-but-live object lingers until its
            // last holder releases.
            if (state.Active.Count == 0 && state.PendingAdmission == 0 && state.InFlight == 1)
            {
                _states.TryRemove(new KeyValuePair<AiKeyId, KeyState>(id, state));
            }
        }
    }

    private sealed class AdmissionToken : IDisposable
    {
        private readonly IDisposable _releaser;
        private readonly KeyState _state;
        private int _done;

        public AdmissionToken(IDisposable releaser, KeyState state)
        {
            _releaser = releaser;
            _state = state;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _done, 1) != 0)
            {
                return;
            }

            try
            {
                _releaser.Dispose();
            }
            finally
            {
                lock (_state.Gate)
                {
                    _state.PendingAdmission--;
                }
            }
        }
    }

    private sealed class Releaser : IDisposable
    {
        private readonly PerKeyConcurrencyTracker _owner;
        private readonly AiKeyId _id;
        private readonly KeyState _state;
        private readonly ModelTier _tier;
        private int _done;

        public Releaser(PerKeyConcurrencyTracker owner, AiKeyId id, KeyState state, ModelTier tier)
        {
            _owner = owner;
            _id = id;
            _state = state;
            _tier = tier;

            // Counts THIS holder from creation until its Release completes,
            // so a removal decision made inside Release knows whether anyone
            // else is concurrently finishing.
            lock (state.Gate)
            {
                state.InFlight++;
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _done, 1) == 0)
            {
                try
                {
                    _owner.Release(_id, _state, _tier);
                }
                finally
                {
                    lock (_state.Gate)
                    {
                        _state.InFlight--;
                    }
                }
            }
        }
    }
}
