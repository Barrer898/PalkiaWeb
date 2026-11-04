using System.Text.Json;
using Microsoft.Extensions.Logging;
using Palkia.Core.Ai;

namespace Palkia.Infra.Ai;

/// <summary>
/// Talks to the per-profile `ft serve` instances over loopback. Deliberately
/// tiny: only the endpoints docs/cli.md guarantees (GET /health, GET
/// /v1/stats), used for lifecycle and dashboard purposes. Chat completions
/// are proxied straight through by the gateway with this client NOT in the
/// path - streaming SSE must not be re-parsed by us.
///
/// /health is exempt from FreeToken's --api-key check ("Require the key on
/// every route except /health"), so monitoring works even if a future build
/// starts requiring keys on the ft side.
/// </summary>
public sealed class FreeTokenClient : IFreeTokenClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;
    private readonly ILogger<FreeTokenClient> _logger;

    public FreeTokenClient(HttpClient http, ILogger<FreeTokenClient> logger)
    {
        _http = http;
        _logger = logger;
    }

    public async Task<FreeTokenHealth> GetHealthAsync(ModelProfile profile, CancellationToken ct = default)
    {
        try
        {
            using var resp = await _http.GetAsync(Base(profile) + "/health", HttpCompletionOption.ResponseContentRead, ct);
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            var root = doc.RootElement;

            var status = root.TryGetProperty("status", out var s) ? s.GetString() ?? "unknown" : "unknown";

            // Loading progress is worth surfacing verbatim - a 35B offload
            // load takes minutes and "Loading, 42% of bytes" beats a spinner.
            var detail = status switch
            {
                "loading" when root.TryGetProperty("progress", out var p)
                    && p.TryGetProperty("done_bytes", out var d) && p.TryGetProperty("total_bytes", out var t)
                    && t.GetInt64() > 0
                    => $"loading weights: {(double)d.GetInt64() / t.GetInt64() * 100:0}% ({d.GetInt64() / 1048576} / {t.GetInt64() / 1048576} MiB)",
                "error" => root.TryGetProperty("message", out var m) ? m.GetString() ?? "error" : "error",
                "ok" => root.TryGetProperty("uptime_s", out var u) ? $"serving {profile.ApiName}, up {u.GetInt32()}s" : "ok",
                _ => status,
            };

            return new FreeTokenHealth(true, status, detail);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "ft /health unreachable for profile {Profile}", profile.Id);
            return new FreeTokenHealth(false, "unreachable", ex.Message);
        }
    }

    public async Task<IReadOnlyDictionary<string, string>?> GetStatsAsync(ModelProfile profile, CancellationToken ct = default)
    {
        try
        {
            using var resp = await _http.GetAsync(Base(profile) + "/v1/stats", HttpCompletionOption.ResponseContentRead, ct);
            if (!resp.IsSuccessStatusCode)
            {
                return null;
            }

            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            var stats = new Dictionary<string, string>();

            Pick(stats, doc, "throughput", "decode_tokens_per_sec");
            Pick(stats, doc, "throughput", "prefill_tokens_per_sec");
            Pick(stats, doc, "running_requests");
            Pick(stats, doc, "waiting_requests");
            if (doc.RootElement.TryGetProperty("vram_bytes", out var v) && v.TryGetInt64(out var vb))
            {
                stats["vram"] = $"{vb / 1048576} MiB";
            }

            return stats.Count > 0 ? stats : null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Not an error worth logging loudly - unreachable/just-started.
            _logger.LogDebug(ex, "ft /v1/stats unavailable for profile {Profile}", profile.Id);
            return null;
        }
    }

    private static string Base(ModelProfile profile) => $"http://127.0.0.1:{profile.Port}";

    private static void Pick(Dictionary<string, string> into, JsonDocument doc, params string[] path)
    {
        JsonElement el = doc.RootElement;
        foreach (var key in path)
        {
            if (!el.TryGetProperty(key, out el))
            {
                return;
            }
        }

        into[string.Join('.', path)] = el.ValueKind == JsonValueKind.Number ? el.GetRawText() : el.ToString();
    }
}
