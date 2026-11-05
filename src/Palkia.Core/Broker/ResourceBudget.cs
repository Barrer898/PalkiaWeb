namespace Palkia.Core.Broker;

/// <summary>
/// The total pool the broker is allowed to hand out, deliberately less than
/// the machine's full physical specs - some headroom always stays reserved
/// for the OS, the app itself, and always-on cheap services (Samba/NFS). See
/// appsettings.json's ResourceBroker section for the actual configured
/// numbers on this box.
/// </summary>
public sealed record ResourceBudget(int CpuCores, int MemoryMb, int VramMb)
{
    public static readonly ResourceBudget Zero = new(0, 0, 0);

    public ResourceBudget Subtract(ResourceBudget other) => new(
        CpuCores - other.CpuCores,
        MemoryMb - other.MemoryMb,
        VramMb - other.VramMb);

    public ResourceBudget Add(ResourceBudget other) => new(
        CpuCores + other.CpuCores,
        MemoryMb + other.MemoryMb,
        VramMb + other.VramMb);

    /// <summary>True if this budget has enough of every dimension to cover <paramref name="request"/>.</summary>
    public bool CanAfford(ResourceBudget request) =>
        CpuCores >= request.CpuCores &&
        MemoryMb >= request.MemoryMb &&
        VramMb >= request.VramMb;
}
