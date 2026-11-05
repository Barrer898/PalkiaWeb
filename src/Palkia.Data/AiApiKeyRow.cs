namespace Palkia.Data;

/// <summary>
/// Persisted row for an admin-managed LLM gateway API key. Deliberately
/// stores ONLY the PBKDF2 hash and an 8-character display prefix - never the
/// secret itself, so a leaked SQLite file (backups! SMB shares!) is not a
/// leaked set of working credentials. The plaintext exists exactly once, in
/// the HTTP response to the admin who created it.
/// </summary>
public sealed class AiApiKeyRow
{
    public Guid Id { get; set; }

    public string Label { get; set; } = string.Empty;

    /// <summary>"salt.iterations.hash", all base64. See DbAiKeyStore.</summary>
    public string KeyHash { get; set; } = string.Empty;

    /// <summary>First 8 chars of the real key, for recognition in the UI only.</summary>
    public string KeyPrefix { get; set; } = string.Empty;

    /// <summary>Empty = every profile the box allows. Comma-separated profile
    /// ids otherwise (medium,small,fast,...). CSV because six household keys
    /// do not need a join table.</summary>
    public string AllowedProfilesCsv { get; set; } = string.Empty;

    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
}
