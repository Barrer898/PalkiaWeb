namespace Palkia.Core.Ai;

/// <summary>
/// The agreed per-API-key concurrency policy (deployment rules §6, as
/// restated in the PR review): ONE active request per key across all
/// profiles, with exactly one exception — one Small request and one Fast
/// request may run simultaneously for the same key. Two requests on the
/// SAME profile are never allowed for one key, and no other cross-profile
/// combination is allowed either.
///
/// This class is the single source of truth for that rule. It used to live
/// as a per-profile <c>MaxConcurrentRequests</c> number (Small=2, Fast=4),
/// which enforced something quite different — per-(key,profile) limits that
/// let one key hold four simultaneous Fast requests plus two Small ones.
/// Admission state itself lives in <see cref="PerKeyConcurrencyTracker"/>;
/// keeping the RULE here makes it directly unit-testable without a broker,
/// OpenRC or an HTTP stack.
/// </summary>
public static class PerKeyConcurrencyPolicy
{
    public const int MaxActiveLarge = 1;   // Max / Big / Medium combined
    public const int MaxActiveSmall = 1;
    public const int MaxActiveFast = 1;

    /// <summary>The largest in-flight set a single key may hold at once:
    /// one Small + one Fast. Used for honest error messages.</summary>
    public const int MaxActiveTotal = MaxActiveLarge + MaxActiveSmall + MaxActiveFast;

    /// <summary>True if a key already holding <paramref name="active"/> may
    /// also take one request on <paramref name="candidate"/>.</summary>
    public static bool Allows(ModelTier candidate, IEnumerable<ModelTier> active)
    {
        var small = 0;
        var fast = 0;
        var large = 0;

        foreach (var tier in active)
        {
            switch (tier)
            {
                case ModelTier.Small: small++; break;
                case ModelTier.Fast: fast++; break;
                default: large++; break;
            }
        }

        return candidate switch
        {
            // A second large request is refused whether or not the key's one
            // free "small+fast" slot pair is currently occupied - the large
            // allowance is its own single seat.
            ModelTier.Max or ModelTier.Big or ModelTier.Medium => large < MaxActiveLarge,
            ModelTier.Small => small < MaxActiveSmall && large < MaxActiveLarge,
            ModelTier.Fast => fast < MaxActiveFast && large < MaxActiveLarge,
            _ => false,
        };
    }

    /// <summary>How many of the key's current requests would have to finish
    /// before <paramref name="candidate"/> could be admitted. Feeds the
    /// Retry-After hint on 429 responses.</summary>
    public static int BlockingCount(ModelTier candidate, IEnumerable<ModelTier> active)
    {
        if (Allows(candidate, active))
        {
            return 0;
        }

        var list = active.ToList();
        switch (candidate)
        {
            case ModelTier.Max or ModelTier.Big or ModelTier.Medium:
                return Math.Max(1, list.Count(t => t is not (ModelTier.Small or ModelTier.Fast)));
            default:
                // Saturated own-tier seat, or the key's one large seat is
                // busy - either way at least one completion unblocks it.
                return 1;
        }
    }
}
