namespace Palkia.Core.Broker;

/// <summary>
/// Single source of truth for "is there room to run this right now". State
/// is in-memory only and reset on app restart - see AdoptExistingLease for
/// how that's reconciled against reality at startup. Registered as a
/// singleton; all mutation happens under one lock, which is completely fine
/// at this scale (a handful of leases, changing a few times an hour at
/// most) and far simpler than trying to make it lock-free.
/// </summary>
public sealed class ResourceBroker : IResourceBroker
{
    private readonly object _lock = new();
    private readonly List<Lease> _active = new();
    private readonly List<Lease> _queued = new(); // FIFO: index 0 is next in line

    public ResourceBroker(ResourceBudget budget)
    {
        Budget = budget;
    }

    public ResourceBudget Budget { get; }

    public ResourceBudget Available
    {
        get
        {
            lock (_lock)
            {
                return AvailableUnlocked();
            }
        }
    }

    public IReadOnlyList<Lease> ActiveLeases
    {
        get { lock (_lock) { return _active.ToList(); } }
    }

    public IReadOnlyList<Lease> QueuedLeases
    {
        get { lock (_lock) { return _queued.ToList(); } }
    }

    public event Action? StateChanged;

    public Lease RequestLease(LeaseRequest request)
    {
        Lease lease;

        lock (_lock)
        {
            if (_active.Any(l => l.Request.ServiceId == request.ServiceId) ||
                _queued.Any(l => l.Request.ServiceId == request.ServiceId))
            {
                throw new LeaseRejectedException(
                    $"'{request.ServiceId}' already has an active or queued lease.");
            }

            if (!Budget.CanAfford(request.Amount))
            {
                throw new LeaseRejectedException(
                    $"'{request.ServiceId}' requested {Describe(request.Amount)}, which exceeds the " +
                    $"total budget of {Describe(Budget)} - this could never be granted, queued or not.");
            }

            lease = new Lease { Id = Guid.NewGuid().ToString("N"), Request = request, State = LeaseState.Queued };

            if (AvailableUnlocked().CanAfford(request.Amount))
            {
                GrantUnlocked(lease);
            }
            else
            {
                _queued.Add(lease);
            }
        }

        StateChanged?.Invoke();
        return lease;
    }

    public void ReleaseLease(string leaseId)
    {
        lock (_lock)
        {
            var active = _active.FirstOrDefault(l => l.Id == leaseId);
            if (active is not null)
            {
                _active.Remove(active);
                active.State = LeaseState.Released;
                active.ReleasedAtUtc = DateTimeOffset.UtcNow;
                PromoteQueuedUnlocked();
            }
            else
            {
                var queued = _queued.FirstOrDefault(l => l.Id == leaseId);
                if (queued is not null)
                {
                    _queued.Remove(queued);
                    queued.State = LeaseState.Released;
                    queued.ReleasedAtUtc = DateTimeOffset.UtcNow;
                }
            }
        }

        StateChanged?.Invoke();
    }

    public Lease AdoptExistingLease(LeaseRequest request)
    {
        Lease lease;

        lock (_lock)
        {
            lease = new Lease { Id = Guid.NewGuid().ToString("N"), Request = request, State = LeaseState.Queued };
            GrantUnlocked(lease);
        }

        StateChanged?.Invoke();
        return lease;
    }

    private ResourceBudget AvailableUnlocked()
    {
        var used = _active.Aggregate(ResourceBudget.Zero, (acc, l) => acc.Add(l.Request.Amount));
        return Budget.Subtract(used);
    }

    private void GrantUnlocked(Lease lease)
    {
        lease.State = LeaseState.Active;
        lease.GrantedAtUtc = DateTimeOffset.UtcNow;
        _active.Add(lease);
    }

    /// <summary>Strict FIFO: only ever considers the head of the queue, so an
    /// earlier, larger request can never be starved by smaller ones behind
    /// it jumping the line.</summary>
    private void PromoteQueuedUnlocked()
    {
        while (_queued.Count > 0 && AvailableUnlocked().CanAfford(_queued[0].Request.Amount))
        {
            var next = _queued[0];
            _queued.RemoveAt(0);
            GrantUnlocked(next);
        }
    }

    private static string Describe(ResourceBudget b) =>
        $"{b.CpuCores} cores / {b.MemoryMb}MB RAM / {b.VramMb}MB VRAM";
}
