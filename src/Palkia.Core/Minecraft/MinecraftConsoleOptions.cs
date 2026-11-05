namespace Palkia.Core.Minecraft;

/// <summary>Bound from the "MinecraftConsole" appsettings section. Paths here
/// must agree with minecraft_dir in deploy/palkia-minecraft.confd and the
/// output_log path in deploy/palkia-minecraft.initd - three more places that
/// all need to say the same thing by hand, same story as MinecraftOptions.</summary>
public sealed class MinecraftConsoleOptions
{
    public string LogPath { get; set; } = "/var/log/minecraft/minecraft.log";
    public string ConsoleFifoPath { get; set; } = "/var/lib/minecraft/console.fifo";
    public int CommandWriteTimeoutSeconds { get; set; } = 3;
}
