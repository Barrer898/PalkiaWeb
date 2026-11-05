namespace Palkia.Core.Ai;

/// <summary>
/// One gateway credential as stored/shown in the UI. The secret itself only
/// travels to the client once, at creation time (<see cref="PlainKeyOnce"/>);
/// every later read goes through <see cref="KeyHash"/>, so a stolen database
/// dump or a casual page refresh never reveals usable keys. Hashed-at-rest is
/// what FreeTokenOptions.ApiKeys (plaintext config entries) always wanted but
/// didn't have - admin-managed keys live here instead.
/// </summary>
public sealed record AiKeyRecord(
    Guid Id,
    string Label,
    string KeyHash,
    List<string> AllowedProfiles,
    DateTimeOffset CreatedAtUtc,
    string? PlainKeyOnce = null)
{
    /// <summary>Display form: first 8 chars + dots. Never the full secret.</summary>
    public string Masked =>
        KeyPrefix.Length <= 8 ? KeyPrefix + "…" : KeyPrefix[..8] + "…";

    /// <summary>First 8 characters of the real key, stored at creation purely
    /// so the list can show something recognizable without being able to
    /// reconstruct the secret.</summary>
    public string KeyPrefix { get; init; } = "";
}

/// <summary>
/// Admin-managed API keys for the LLM gateway. Deliberately NOT part of
/// FreeTokenOptions: that object is a singleton snapshot bound from config at
/// startup, while these must be mutable at runtime and survive restarts.
/// Implementations raise <see cref="Changed"/> after every mutation so the
/// authenticator can rebuild its comparison set without polling.
/// </summary>
public interface IAiKeyStore
{
    IReadOnlyList<AiKeyRecord> Keys { get; }

    /// <summary>Load persisted keys into the in-memory auth snapshot. Called
    /// once at startup, before the gateway can accept a request - until it
    /// runs the snapshot is empty and every key would be rejected.</summary>
    Task LoadAsync(CancellationToken ct = default);

    /// <summary>Returns the created record with <see cref="AiKeyRecord.PlainKeyOnce"/>
    /// populated - the ONLY moment the plaintext exists server-side.</summary>
    Task<AiKeyRecord> CreateAsync(string label, List<string> allowedProfiles, CancellationToken ct = default);

    Task<bool> DeleteAsync(Guid id, CancellationToken ct = default);

    /// <summary>True when <paramref name="plainKey"/> matches some stored key
    /// (constant-time). Also returns the matched record so callers know the
    /// caller's profile allow-list.</summary>
    bool TryMatch(string plainKey, out AiKeyRecord? record);

    event Action? Changed;
}
