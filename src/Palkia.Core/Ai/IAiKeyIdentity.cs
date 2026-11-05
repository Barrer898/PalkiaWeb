namespace Palkia.Core.Ai;

/// <summary>
/// Stable identity for an authenticated gateway caller. PR review item 7:
/// per-key concurrency MUST be tracked by something that survives renames
/// and doesn't collide across sources — a display label ("workstation",
/// "unnamed-key") is neither stable nor unique, so it can't be the key of
/// the concurrency ledger. Config-file keys carry their plaintext secret
/// (unique within the config section); DB-managed keys carry their row Guid.
/// </summary>
public readonly record struct AiKeyId(string Source, string Value)
{
    /// <summary>
    /// Canonical identity for any presented key. The namespace comes from a
    /// prefix over the secret itself ("palkia-" = admin-managed DB keys,
    /// anything else = config-file keys), so two sources can never collide on
    /// the same secret value — which also means a stolen DB row prefix can't
    /// impersonate a config key's concurrency budget. Display labels are NOT
    /// used: they're neither stable (renames) nor unique ("unnamed-key").
    /// </summary>
    public static AiKeyId ForSecret(string secret) =>
        secret.StartsWith("palkia-", StringComparison.Ordinal)
            ? new AiKeyId("db", secret)
            : new AiKeyId("cfg", secret);

    public override string ToString() => $"{Source}:{Value}";
}
