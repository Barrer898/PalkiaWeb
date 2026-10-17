namespace Palkia.Core.Broker;

/// <summary>
/// A request to hold a chunk of the shared resource pool for as long as some
/// service is running. Identical in shape whether the requester is Minecraft
/// today or the AI/VM/Kdenlive modules later - the broker doesn't need to
/// know what a "service" actually is, only how much CPU/RAM/VRAM it wants.
/// </summary>
public sealed record LeaseRequest(string ServiceId, string DisplayName, ResourceBudget Amount);

public enum LeaseState
{
    /// <summary>Waiting for enough free budget - will be granted in FIFO order as things are released.</summary>
    Queued,

    /// <summary>Holding its reserved share of the budget right now.</summary>
    Active,

    /// <summary>Voluntarily given back (service stopped normally).</summary>
    Released,
}

public sealed class Lease
{
    public required string Id { get; init; }
    public required LeaseRequest Request { get; init; }
    public LeaseState State { get; set; }
    public DateTimeOffset RequestedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? GrantedAtUtc { get; set; }
    public DateTimeOffset? ReleasedAtUtc { get; set; }
}
