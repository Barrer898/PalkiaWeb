using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Palkia.Core.Ai;
using Palkia.Data;

namespace Palkia.Infra.Ai;

/// <summary>
/// EF-backed store for admin-managed gateway keys. The plaintext secret is
/// generated here, returned exactly once (PlainKeyOnce) and never stored -
/// only a PBKDF2 hash (plus an 8-char display prefix) hits SQLite. Auth on
/// the hot path reads the in-memory snapshot rebuilt after each mutation or
/// load, so per-request verification never touches the database.
/// </summary>
public sealed class DbAiKeyStore : IAiKeyStore
{
    private readonly IDbContextFactory<PalkiaDbContext> _dbFactory;
    private volatile List<AiKeyRecord> _snapshot = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public DbAiKeyStore(IDbContextFactory<PalkiaDbContext> dbFactory)
    {
        _dbFactory = dbFactory;
    }

    public IReadOnlyList<AiKeyRecord> Keys => _snapshot;

    public event Action? Changed;

    /// <summary>Load persisted keys into the auth snapshot. Called once at
    /// startup before the gateway can serve anything.</summary>
    public async Task LoadAsync(CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var rows = await db.AiApiKeys.AsNoTracking().ToListAsync(ct);
        _snapshot = rows.Select(ToRecord).ToList();
        Changed?.Invoke();
    }

    public async Task<AiKeyRecord> CreateAsync(string label, List<string> allowedProfiles, CancellationToken ct = default)
    {
        await _writeLock.WaitAsync(ct);
        try
        {
            var plain = "palkia-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
            var record = new AiApiKeyRow
            {
                Id = Guid.NewGuid(),
                Label = string.IsNullOrWhiteSpace(label) ? "unnamed" : label.Trim(),
                KeyHash = Hash(plain),
                KeyPrefix = plain[..8],
                AllowedProfilesCsv = string.Join(',', allowedProfiles ?? new List<string>()),
                CreatedAtUtc = DateTimeOffset.UtcNow,
            };

            await using (var db = await _dbFactory.CreateDbContextAsync(ct))
            {
                db.AiApiKeys.Add(record);
                await db.SaveChangesAsync(ct);
            }

            await ReloadSnapshotAsync(ct);
            return ToRecord(record) with { PlainKeyOnce = plain };
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken ct = default)
    {
        await _writeLock.WaitAsync(ct);
        try
        {
            int removed;
            await using (var db = await _dbFactory.CreateDbContextAsync(ct))
            {
                removed = await db.AiApiKeys.Where(k => k.Id == id).ExecuteDeleteAsync(ct);
            }

            if (removed > 0)
            {
                await ReloadSnapshotAsync(ct);
            }

            return removed > 0;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public bool TryMatch(string plainKey, out AiKeyRecord? record)
    {
        // Every stored hash gets a full PBKDF2 comparison - no early exit -
        // so response timing doesn't reveal WHICH key matched or that none
        // did until the very end. Cheap at ~dozens of keys; if that ever
        // stops being true, cache derived-verification state instead of
        // weakening this.
        AiKeyRecord? match = null;
        var plainBytes = System.Text.Encoding.UTF8.GetBytes(plainKey);

        foreach (var candidate in _snapshot)
        {
            if (Verify(plainBytes, candidate.KeyHash))
            {
                match = candidate;
            }
        }

        record = match;
        return match is not null;
    }

    private async Task ReloadSnapshotAsync(CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var rows = await db.AiApiKeys.AsNoTracking().ToListAsync(ct);
        _snapshot = rows.Select(ToRecord).ToList();
        Changed?.Invoke();
    }

    private static AiKeyRecord ToRecord(AiApiKeyRow row) => new(
        row.Id,
        row.Label,
        row.KeyHash,
        (row.AllowedProfilesCsv ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList(),
        row.CreatedAtUtc)
    {
        KeyPrefix = row.KeyPrefix,
    };

    // PBKDF2-SHA256, 100k iterations, random salt embedded in the stored
    // string (salt.iterations.hash, all base64). API keys are low-entropy-ish
    // secrets worth slowing down offline guesses on, and this needs no extra
    // package - same primitive Identity itself uses for password hashing.
    private const int Iterations = 100_000;

    private static string Hash(string plain)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var derived = Rfc2898DeriveBytes.Pbkdf2(plain, salt, Iterations, HashAlgorithmName.SHA256, 32);
        return $"{Convert.ToBase64String(salt)}.{Iterations}.{Convert.ToBase64String(derived)}";
    }

    private static bool Verify(byte[] plain, string stored)
    {
        var parts = stored.Split('.');
        if (parts.Length != 3 || !int.TryParse(parts[1], out var iterations))
        {
            return false;
        }

        try
        {
            var salt = Convert.FromBase64String(parts[0]);
            var expected = Convert.FromBase64String(parts[2]);
            var actual = Rfc2898DeriveBytes.Pbkdf2(plain, salt, iterations, HashAlgorithmName.SHA256, expected.Length);
            return CryptographicOperations.FixedTimeEquals(expected, actual);
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
