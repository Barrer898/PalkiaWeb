namespace Palkia.Core.Ai;

/// <summary>
/// The role a profile plays in the five-profile lineup. Each tier has one
/// slot in the broker: only one profile per tier can hold a lease at a time,
/// which is what enforces "never two large profiles side by side" without
/// the broker needing to know anything about model sizes. See
/// Palkia_FreeToken_Deployment_Rules.md sections 3 and 6.
/// </summary>
public enum ModelTier
{
    /// <summary>Qwen3.6-35B-A3B - experimental, MoE offload, exclusive slot.</summary>
    Max,

    /// <summary>GPT-OSS-20B - large general-purpose MoE, exclusive slot.</summary>
    Big,

    /// <summary>Qwen3.5-9B - everyday coding/chat/agent work.</summary>
    Medium,

    /// <summary>Qwen3.5-2B - lightweight tasks. May coexist with Fast.</summary>
    Small,

    /// <summary>Qwen3.5-0.8B - routing/classification/extraction. May coexist with Small.</summary>
    Fast,
}

/// <summary>How FreeToken places an MoE model's experts (ft serve --moe-strategy).
/// Dense models always resolve to Fused; MoE models start at Offload per the
/// deployment rules ("MoE offload first, hybrid benchmarked as an alternative").</summary>
public enum MoEPlacement
{
    /// <summary>Let FreeToken's auto rule decide (dense -> fused, MoE -> offload).</summary>
    Auto,

    /// <summary>Experts resident on GPU - dense models that fit, or small MoE.</summary>
    Fused,

    /// <summary>Experts live in pinned host RAM, LRU expert cache on GPU, misses stream over PCIe.</summary>
    Offload,

    /// <summary>Split each step between PCIe fetches and CPU compute. Requires an `ft bench bw` calibration first.</summary>
    Hybrid,

    /// <summary>All misses computed on CPU. Avoid as a default on Palkia's older 4-core CPU.</summary>
    Cpu,
}

/// <summary>
/// One selectable model profile, mirroring the agreed Palkia lineup: exactly
/// five tiers, Qwen-first, long-context targets, loaded on demand rather
/// than kept resident. Everything here maps onto flags of
/// `ft serve ... --served-model-name &lt;ApiName&gt;` (see docs/cli.md in the
/// FreeToken repo) plus the broker lease numbers Palkia itself enforces.
///
/// The numbers are deliberately duplicated in three places that must agree
/// by hand, same pattern as MinecraftOptions: the launch script's ft flags,
/// the cgroup limits in /etc/conf.d/palkia-ft-&lt;profile&gt;, and this
/// config's lease amounts. The broker decides *when* a server may start;
/// the cgroup is what actually caps it while running.
/// </summary>
public sealed class ModelProfile
{
    public required string Id { get; init; }

    /// <summary>Tier label shown in the UI (Max/Big/Medium/Small/Fast).</summary>
    public string TierLabel => Tier switch
    {
        ModelTier.Max => "Max",
        ModelTier.Big => "Big",
        ModelTier.Medium => "Medium",
        ModelTier.Small => "Small",
        ModelTier.Fast => "Fast",
        _ => Tier.ToString(),
    };

    public ModelTier Tier { get; init; }

    public string DisplayName { get; init; } = "";

    public string Description { get; init; } = "";

    /// <summary>Local checkpoint directory or HF repo id passed to
    /// `ft serve --model`. FTW dirs are auto-detected by FreeToken.</summary>
    public required string ModelPath { get; init; }

    /// <summary>The id FreeToken reports on /v1/models and that clients put in
    /// the request's "model" field (--served-model-name).</summary>
    public required string ApiName { get; init; }

    /// <summary>Loopback port this profile's `ft serve` instance binds to.
    /// Never exposed directly - nginx authenticates and proxies to it.</summary>
    public int Port { get; init; }

    /// <summary>Target context length in tokens (--max-seq-len-override). A
    /// test target, not a guaranteed working limit - see deployment rules §4.</summary>
    public int ContextTokens { get; init; }

    /// <summary>Chunked-prefill chunk size in tokens (--max-prefill-length).</summary>
    public int MaxPrefillLength { get; init; } = 8192;

    /// <summary>Default output budget for requests that omit one
    /// (--max-output-tokens).</summary>
    public int MaxOutputTokens { get; init; } = 8192;

    /// <summary>Expert placement strategy (--moe-strategy).</summary>
    public MoEPlacement MoE { get; init; } = MoEPlacement.Auto;

    /// <summary>Server-wide concurrent request cap (--max-running-requests),
    /// mirrored into the broker's MaxConcurrentRequests. FreeToken queues
    /// internally above this; Palkia's per-user semaphore bounds what reaches
    /// it so one person can't fill the whole queue.</summary>
    public int MaxRunningRequests { get; init; } = 1;

    /// <summary>Per-user in-flight proxy limit enforced by Palkia itself.
    /// Large profiles stay at 1; Small+Fast tolerate a bit more per user.</summary>
    public int MaxConcurrentRequests { get; init; } = 1;

    /// <summary>Broker lease: CPU cores reserved while this server runs.</summary>
    public int LeaseCpuCores { get; init; }

    /// <summary>Broker lease: host RAM reserved (weights + pinned expert banks
    /// + runtime). Must match the cgroup memory.max in the confd file.</summary>
    public int LeaseMemoryMb { get; init; }

    /// <summary>Broker lease: VRAM reserved (resident weights + expert cache + KV).</summary>
    public int LeaseVramMb { get; init; }

    /// <summary>The OpenRC service name, e.g. palkia-ft-medium. Matches the
    /// init script installed from deploy/palkia-ft@.initd and the wrapper's
    /// allow-list entry.</summary>
    public string RcName => $"palkia-ft-{Id}";

    /// <summary>Shared secret handed to `ft serve --api-key` by the launcher
    /// (from /etc/palkia/freetoken/keys.env). Defense in depth behind the
    /// loopback bind: even something already on the box must present it to
    /// talk to ft directly. The gateway injects this header itself - client
    /// keys never travel upstream. Empty = no key set on ft. Populated at
    /// startup from FreeTokenOptions.UpstreamApiKeys, not baked into the
    /// catalog, so secrets stay out of committed source.</summary>
    public string UpstreamApiKey { get; set; } = "";

    /// <summary>True if this profile is an MoE whose experts need host-RAM
    /// placement decisions (offload/hybrid) rather than plain GPU residency.</summary>
    public bool IsMoE => MoE is MoEPlacement.Offload or MoEPlacement.Hybrid or MoEPlacement.Cpu;
}
