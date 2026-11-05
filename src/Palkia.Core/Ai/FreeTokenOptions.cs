namespace Palkia.Core.Ai;

/// <summary>
/// Bound from the "FreeToken" appsettings section. Paths here must agree
/// with deploy/palkia-ft-launch.sh's defaults and the confd files installed
/// to /etc/conf.d/palkia-ft-&lt;profile&gt; - the same "three places say the
/// same number by hand" pattern as MinecraftOptions.
/// </summary>
public sealed class FreeTokenOptions
{
    /// <summary>Directory holding the profile config files that the launcher
    /// script sources (installed copies: palkia-ft-&lt;id&gt;.conf).</summary>
    public string ProfileConfigDir { get; set; } = "/etc/palkia/freetoken";

    /// <summary>Where checkpoints (HF safetensors or converted FTW dirs) live.
    /// NVMe on Palkia - storage and loading, never a substitute for RAM.</summary>
    public string ModelsDir { get; set; } = "/var/lib/palkia-models";

    /// <summary>Host the managed `ft serve` instances bind to. Loopback only:
    /// Palkia's authenticated proxy is the only thing that should ever reach
    /// it (deployment rules §8: no unauthenticated direct exposure).</summary>
    public string BindHost { get; set; } = "127.0.0.1";

    /// <summary>Seconds a profile may stay idle (no proxied requests) before
    /// Palkia stops it and releases its lease. 0 disables auto-shutdown.
    /// Loading a 35B MoE from NVMe takes minutes - don't set this too low.</summary>
    public int IdleShutdownMinutes { get; set; } = 15;

    /// <summary>Profiles started at boot and kept resident regardless of the
    /// idle timer, so the first agent request of the day doesn't eat a cold
    /// NVMe load. Only tiny profiles belong here on Palkia's budget - e.g.
    /// ["fast"] or ["small","fast"], which the lease sizes allow to coexist.
    /// Anything listed must also be enabled in /etc/conf.d/palkia-ft-&lt;id&gt;.</summary>
    public List<string> AlwaysOnProfiles { get; set; } = new();

    /// <summary>How long to wait for /health to answer "ok" after a start
    /// before declaring the start failed. Large MoE loads from NVMe are slow;
    /// this covers the worst case (Max profile).</summary>
    public int LoadTimeoutSeconds { get; set; } = 900;

    /// <summary>Client API keys accepted by the Palkia proxy endpoint
    /// (/api/llm/v1/*). Separate credential per agent/user where practical
    /// (deployment rules §8); admins manage them on the AI page.</summary>
    public List<AiApiKey> ApiKeys { get; set; } = new();

    /// <summary>Per-profile loopback shared secrets (ft's own --api-key), keyed
    /// by profile id ("medium", "fast", ...). Loaded from the
    /// FreeToken:UpstreamApiKeys section (env form FreeToken__UpstreamApiKeys__&lt;id&gt;). Must
    /// match what palkia-ft-launch.sh reads from keys.env - same
    /// "three places agree by hand" pattern as everything else here.</summary>
    public Dictionary<string, string> UpstreamApiKeys { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// One client credential for the OpenAI-compatible endpoint. Stored in
/// appsettings/env because there are ~6 household accounts and inventing a
/// second credential store before it's needed would be guessing; move to
/// hashed-at-rest DB records when self-service key management is added.
/// </summary>
public sealed class AiApiKey
{
    public string Label { get; set; } = "";

    /// <summary>Shared-secret token compared in constant time. Never logged.</summary>
    public string Key { get; set; } = "";

    /// <summary>Empty = every profile the box allows. Otherwise the profile
    /// ids (medium/small/fast/...) this key may use.</summary>
    public List<string> AllowedProfiles { get; set; } = new();
}
