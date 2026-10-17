namespace Palkia.Core.Metrics;

/// <summary>
/// A point-in-time read of host resource usage, independent of the broker's
/// leases (the broker tracks what it *thinks* is allocated; this is what's
/// actually happening on the box - useful for sanity-checking the broker's
/// budget against reality, e.g. noticing something outside Palkia's control
/// is eating CPU). All fields nullable: CPU% needs two samples so is null on
/// the very first tick after startup, and GPU fields are null outright on
/// any box without nvidia-smi available.
/// </summary>
public sealed record SystemMetricsSnapshot(
    double? CpuUsagePercent,
    long? RamUsedMb,
    long? RamTotalMb,
    long? SwapFreeMb,
    long? SwapTotalMb,
    double? GpuUsagePercent,
    long? GpuVramUsedMb,
    long? GpuVramTotalMb,
    DateTimeOffset SampledAtUtc)
{
    public static readonly SystemMetricsSnapshot Empty =
        new(null, null, null, null, null, null, null, null, DateTimeOffset.MinValue);
}

public interface ISystemMetricsProvider
{
    /// <summary>Returns the most recent sample immediately - never blocks on
    /// actually taking a new reading, since that happens on a background
    /// timer. Call this as often as you like (e.g. every dashboard render).</summary>
    SystemMetricsSnapshot GetLatest();
}
