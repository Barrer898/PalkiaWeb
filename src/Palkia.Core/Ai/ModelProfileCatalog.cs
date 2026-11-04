namespace Palkia.Core.Ai;

/// <summary>
/// The agreed five-profile Palkia lineup (deployment rules §3/§11), with the
/// lease sizes and context targets from the rollout plan. These are starting
/// points to validate against the installed FreeToken build and the actual
/// quantized checkpoints - not guaranteed working limits. Qwen-first, long
/// context as a hard requirement (8K is explicitly too small for agents).
///
/// Like ManagedServiceCatalog, deliberately hardcoded rather than free-text
/// config: every entry here must correspond to an init script + confd file
/// the operator reviewed and installed, and to allow-list entries in the
/// rc-ctl wrapper. A profile that exists only in appsettings grants nothing.
/// </summary>
public static class ModelProfileCatalog
{
    public static readonly IReadOnlyList<ModelProfile> All = new[]
    {
        // Max / Experimental - MoE, offload first, exclusive slot, one big
        // request at a time. 16GB host RAM is tight for this; expect it to be
        // the last thing enabled and the first thing to fall back (rules §7).
        new ModelProfile
        {
            Id = "max",
            Tier = ModelTier.Max,
            DisplayName = "Qwen3.6-35B-A3B (Max / experimental)",
            Description = "Highest-capability Qwen. MoE expert offload - needs almost all spare RAM, keep swap as cushion only.",
            ModelPath = "/var/lib/palkia-models/Qwen3.6-35B-A3B",
            ApiName = "qwen3.6-35b-a3b",
            Port = 1923,
            ContextTokens = 110_576,      // reproduce the 110K achieved on PorygonZ; test 128K only after this is stable
            MaxPrefillLength = 4096,      // smaller chunks: prefill under offload is PCIe-bound anyway
            MaxOutputTokens = 8192,
            MoE = MoEPlacement.Offload,
            MaxRunningRequests = 1,       // start server-wide 1; two only after stable headroom (rules §6)
            MaxConcurrentRequests = 1,
            LeaseCpuCores = 2,            // CPU gather threads for streaming experts over PCIe
            LeaseMemoryMb = 10240,        // full expert pool pinned in host RAM + runtime; NOT all of it
            LeaseVramMb = 10240,          // resident non-expert weights + expert cache + KV
        },

        // Big - gpt-oss-20b is MXFP4 MoE; offload first, benchmark hybrid
        // against it with identical model/quant/context/concurrency (§5).
        new ModelProfile
        {
            Id = "big",
            Tier = ModelTier.Big,
            DisplayName = "GPT-OSS-20B (Big)",
            Description = "Large general-purpose MoE. Offload by default; run `ft bench bw` before trying hybrid.",
            ModelPath = "/var/lib/palkia-models/gpt-oss-20b",
            ApiName = "gpt-oss-20b",
            Port = 1922,
            ContextTokens = 65_536,       // begin at 64K, then test 96K/128K if memory allows
            MaxPrefillLength = 4096,
            MaxOutputTokens = 8192,
            MoE = MoEPlacement.Offload,
            MaxRunningRequests = 1,
            MaxConcurrentRequests = 1,
            LeaseCpuCores = 2,
            LeaseMemoryMb = 9216,         // ~13GB MXFP4 weights mostly in host RAM + runtime
            LeaseVramMb = 8192,           // leaves room for other caches/KV on the 12GB card
        },

        // Medium - the everyday workhorse and the FIRST profile to roll out
        // (validation order §9 step 2). Dense: GPU-resident if the chosen
        // quantization fits, which is why its lease claims nearly all VRAM.
        new ModelProfile
        {
            Id = "medium",
            Tier = ModelTier.Medium,
            DisplayName = "Qwen3.5-9B (Medium)",
            Description = "Everyday coding, chat and agent work. Dense/GPU-resident; establish the stable baseline here first.",
            ModelPath = "/var/lib/palkia-models/Qwen3.5-9B",
            ApiName = "qwen3.5-9b",
            Port = 1921,
            ContextTokens = 65_536,       // begin 64K, then test 96K-110K
            MaxPrefillLength = 8192,
            MaxOutputTokens = 8192,
            MoE = MoEPlacement.Auto,      // dense -> auto resolves to fused
            MaxRunningRequests = 1,
            MaxConcurrentRequests = 1,
            LeaseCpuCores = 1,
            LeaseMemoryMb = 3072,         // python/torch runtime + mmap'd pages, no expert banks
            LeaseVramMb = 10240,          // weights (~5-9GB by quant) + KV for 64K ctx
        },

        // Small - lightweight tasks/subtasks. May coexist with Fast ONLY if
        // testing confirms both fit with safe headroom (§6); the tier-slot
        // rule already keeps them apart from each other's tier.
        new ModelProfile
        {
            Id = "small",
            Tier = ModelTier.Small,
            DisplayName = "Qwen3.5-2B (Small)",
            Description = "Lightweight general tasks and agent subtasks. GPU-resident.",
            ModelPath = "/var/lib/palkia-models/Qwen3.5-2B",
            ApiName = "qwen3.5-2b",
            Port = 1920,
            ContextTokens = 65_536,       // begin around 64K
            MaxPrefillLength = 8192,
            MaxOutputTokens = 4096,
            MoE = MoEPlacement.Auto,
            MaxRunningRequests = 2,
            MaxConcurrentRequests = 2,
            LeaseCpuCores = 1,
            LeaseMemoryMb = 2048,
            LeaseVramMb = 4096,           // ~4GB bf16 weights + KV; sized so Small+Fast can coexist
        },

        // Fast - routing/classification/extraction. Cheapest lease in the
        // house; safe to leave running while a human drives another profile.
        new ModelProfile
        {
            Id = "fast",
            Tier = ModelTier.Fast,
            DisplayName = "Qwen3.5-0.8B (Fast)",
            Description = "Routing, classification, extraction, quick answers. GPU-resident, low latency.",
            ModelPath = "/var/lib/palkia-models/Qwen3.5-0.8B",
            ApiName = "qwen3.5-0.8b",
            Port = 1919,                  // FreeToken's default port - natural home for the quick model
            ContextTokens = 32_768,       // begin 32K, then test 64K
            MaxPrefillLength = 8192,
            MaxOutputTokens = 4096,
            MoE = MoEPlacement.Auto,
            MaxRunningRequests = 4,       // FreeToken default; tiny model, cheap requests
            MaxConcurrentRequests = 4,
            LeaseCpuCores = 1,
            LeaseMemoryMb = 1536,
            LeaseVramMb = 3072,
        },
    };

    public static ModelProfile? Find(string id) =>
        All.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));
}
