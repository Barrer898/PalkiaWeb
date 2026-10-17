namespace Palkia.Core.Services;

/// <summary>Bound from the "Minecraft" appsettings section. Keep MemoryMb in
/// agreement with the -Xmx flag in the actual server start script and the
/// rc_cgroup_settings memory.max in deploy/palkia-minecraft.confd - none of
/// these are derived from each other, they're three places that all need to
/// say the same number by hand. See deploy/README.md.</summary>
public sealed class MinecraftOptions
{
    public int CpuCores { get; set; } = 2;
    public int MemoryMb { get; set; } = 6144;
}
