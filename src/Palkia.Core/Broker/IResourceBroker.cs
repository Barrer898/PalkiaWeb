namespace Palkia.Core.Broker;

/// <summary>
/// Thrown when a lease request is rejected outright rather than queued -
/// e.g. the same service already holds or is waiting on a lease, or the
/// request alone exceeds the total budget and could never be granted no
/// matter how long it waits.
/// </summary>
public sealed class LeaseRejectedException : Exception
{
    public LeaseRejectedException(string message) : base(message)
    {
    }
}

public interface IResourceBroker
{
    ResourceBudget Budget { get; }

    /// <summary>Budget - sum of all Active leases. Can go negative from the caller's perspective only conceptually; in practice never does, since admission control enforces it.</summary>
    ResourceBudget Available { get; }

    IReadOnlyList<Lease> ActiveLeases { get; }

    IReadOnlyList<Lease> QueuedLeases { get; }

    /// <summary>
    /// Requests a lease. Returns immediately - the returned Lease may already
    /// be Active (if the budget covered it right away) or Queued (if not; it
    /// will be granted automatically, in FIFO order, as other leases are
    /// released). Callers that care about the outcome should watch
    /// <see cref="StateChanged"/> or poll the returned Lease's State.
    /// </summary>
    Lease RequestLease(LeaseRequest request);

    /// <summary>Gives back a lease. If Queued, just removes it from the queue. If Active, frees its share and promotes any queued leases that now fit.</summary>
    void ReleaseLease(string leaseId);

    /// <summary>
    /// Used only at startup to reconcile broker state with a service that
    /// was already running before the app started (e.g. app restarted while
    /// Minecraft was up). Grants immediately regardless of available budget -
    /// the service is already consuming these resources whether the broker
    /// agreed to it or not, so the alternative is the broker's accounting
    /// being wrong, not the resources being free.
    /// </summary>
    Lease AdoptExistingLease(LeaseRequest request);

    /// <summary>Fires after any change to ActiveLeases/QueuedLeases, so Blazor components can re-render.</summary>
    event Action? StateChanged;
}
