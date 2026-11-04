using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using Palkia.Core.Ai;

namespace Palkia.Infra.Ai;

/// <summary>
/// The OpenAI/Anthropic-compatible gateway every external client (coding
/// agents on the tailnet) and the website chat page go through. Mounted at
/// /api/llm - see deploy/TAILSCALE.md for why the public side of this is
/// Tailscale Serve, not nginx or a port opened to the internet.
///
/// Pipeline per request:
///   1. API-key auth (Palkia's own layer - FreeToken binds to loopback only,
///      so nothing reaches it that hasn't passed here);
///   2. resolve "model" -> profile via AiRequestRouter;
///   3. admission through IAiServingManager (tier lease + per-key
///      concurrency + bounded cold-load wait);
///   4. replay the EXACT inbound bytes to ft's /v1/* route and pipe the
///      upstream response back byte-for-byte - SSE is never re-parsed.
/// </summary>
public static class AiGatewayEndpoints
{
    /// <summary>Hard ceiling on a single inbound request body. A 110K-token
    /// agent prompt with tool schemas is well under this; anything bigger is
    /// either a mistake or somebody aiming at the box's RAM.</summary>
    public const long MaxRequestBodyBytes = 64L * 1024 * 1024;

    /// <summary>How long a gateway request may wait for a cold model load /
    /// queue promotion before getting 503. Agents set generous HTTP timeouts;
    /// loading Qwen3.6-35B off NVMe takes minutes (§7).</summary>
    public const int DefaultMaxWaitMs = 15 * 60 * 1000;

    public static void MapAiGateway(this WebApplication app)
    {
        var group = app.MapGroup("/api/llm");

        // Model discovery needs a key too (it's served by us, not ft), but no
        // lease - clients use it to pick a profile name.
        group.MapGet("/v1/models", HandleModelsAsync);
        group.MapGet("/v1/stats", HandleStatsAsync);

        // OpenAI surface.
        group.MapPost("/v1/chat/completions", (HttpContext http) => ProxyAsync(http, "chat/completions"));
        group.MapPost("/v1/completions", (HttpContext http) => ProxyAsync(http, "completions"));
        group.MapPost("/v1/embeddings", (HttpContext http) => ProxyAsync(http, "embeddings"));

        // Anthropic surface (Claude Code et al. point at /v1/messages).
        group.MapPost("/v1/messages", (HttpContext http) => ProxyAsync(http, "messages"));
    }

    private static IResult HandleModelsAsync(HttpRequest request, IAiServingManager manager, FreeTokenOptions options)
    {
        var auth = ApiKeyAuthenticator.Authenticate(options, KeyStore(request), request);
        if (!auth.Succeeded)
        {
            return Result(auth.ErrorBody!, auth.StatusCode);
        }

        var data = ModelProfileCatalog.All
            .Where(auth.Caller!.Allows)
            .Select(p => new
            {
                id = p.ApiName,
                @object = "model",
                created = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                owned_by = $"palkia-{p.TierLabel.ToLowerInvariant()}",
            });

        return Results.Json(new { @object = "list", data });
    }

    private static IResult HandleStatsAsync(HttpRequest request, IAiServingManager manager, FreeTokenOptions options)
    {
        var auth = ApiKeyAuthenticator.Authenticate(options, KeyStore(request), request);
        if (!auth.Succeeded)
        {
            return Result(auth.ErrorBody!, auth.StatusCode);
        }

        var profiles = ModelProfileCatalog.All
            .Where(auth.Caller!.Allows)
            .Select(p =>
            {
                var st = manager.GetState(p);
                return new
                {
                    id = p.Id,
                    api_name = p.ApiName,
                    tier = p.TierLabel,
                    state = st.State.ToString().ToLowerInvariant(),
                    health = st.HealthDetail,
                    queue_position = st.QueuePosition,
                    last_used_utc = st.LastUsedUtc?.ToString("O"),
                    stats = st.Stats,
                };
            });

        return Results.Json(new { @object = "palkia.profiles", data = profiles });
    }

    private static async Task<IResult> ProxyAsync(HttpContext http, string restPath)
    {
        var manager = http.RequestServices.GetRequiredService<IAiServingManager>();
        var options = http.RequestServices.GetRequiredService<FreeTokenOptions>();
        var logger = http.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("AiGateway");
        var ct = http.RequestAborted;

        var auth = ApiKeyAuthenticator.Authenticate(options, http.RequestServices.GetService<IAiKeyStore>(), http.Request);
        if (!auth.Succeeded)
        {
            return Result(auth.ErrorBody!, auth.StatusCode);
        }

        var caller = auth.Caller!;

        // Buffer just enough to read "model" - bodies can be huge prompts, so
        // cap first, then peek, then replay the EXACT bytes upstream without
        // re-serializing (preserves tool definitions, unknown fields, all of
        // it).
        if (http.Request.ContentLength > MaxRequestBodyBytes)
        {
            return Result(AiRequestRouter.OpenAiError("invalid_request",
                $"Request body exceeds the {MaxRequestBodyBytes / 1048576} MiB limit."), 413);
        }

        using var buffered = new MemoryStream();
        await http.Request.Body.CopyToAsync(buffered, ct);
        if (buffered.Length > MaxRequestBodyBytes)
        {
            return Result(AiRequestRouter.OpenAiError("invalid_request",
                $"Request body exceeds the {MaxRequestBodyBytes / 1048576} MiB limit."), 413);
        }

        buffered.Position = 0;
        string? requestedModel;
        try
        {
            requestedModel = await AiRequestRouter.PeekRequestedModelAsync(buffered, ct);
        }
        catch (JsonException)
        {
            return Result(AiRequestRouter.OpenAiError("invalid_request", "Request body is not valid JSON."), 400);
        }
        buffered.Position = 0;

        var slot = await manager.AcquireSlotAsync(caller.Label, requestedModel ?? "", DefaultMaxWaitMs, ct);
        using (slot)   // releases the per-key permit when the response is fully copied (or on any early exit)
        {
            if (slot.Upstream is null)
            {
                return Result(slot.ErrorBody!, slot.StatusCode);
            }

            var profile = ModelProfileCatalog.Find(slot.ProfileId)!;
            if (!caller.Allows(profile))
            {
                return Result(AiRequestRouter.OpenAiError("permission_error",
                    $"This API key may not use profile '{profile.Id}'."), 403);
            }

            var target = $"http://{options.BindHost}:{profile.Port}/v1/{restPath}{http.Request.QueryString}";

            using var upstreamReq = new HttpRequestMessage(HttpMethod.Post, target)
            {
                Content = new StreamContent(buffered),
            };
            AiRequestRouter.CopyRequestHeaders(http.Request, upstreamReq);
            // ft runs with its own --api-key (see palkia-ft-launch.sh): the
            // client's Palkia key must NOT travel upstream; ours does.
            if (!string.IsNullOrEmpty(profile.UpstreamApiKey))
            {
                upstreamReq.Headers.TryAddWithoutValidation("Authorization", $"Bearer {profile.UpstreamApiKey}");
            }

            HttpResponseMessage upstream;
            try
            {
                upstream = await GatewayHttp.SendAsync(upstreamReq, HttpCompletionOption.ResponseHeadersRead, ct);
            }
            catch (HttpRequestException ex)
            {
                logger.LogWarning(ex, "ft upstream unreachable for profile {Profile} ({Target})", profile.Id, target);
                return Result(AiRequestRouter.OpenAiError("upstream_error",
                    $"The {profile.ApiName} server is not reachable. It may have crashed - check the AI page."), 502);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return Result(AiRequestRouter.OpenAiError("upstream_timeout",
                    $"{profile.ApiName} did not respond within the proxy timeout."), 504);
            }

            using (upstream)
            {
                // CopyResponseAsync writes straight onto http.Response - once
                // the response has started, minimal-API sees that and skips
                // executing the returned IResult, so the release below happens
                // AFTER the full stream has gone out.
                await AiRequestRouter.CopyResponseAsync(upstream, http, ct);
            }
        }

        return EmptyResult.Instance;
    }

    /// <summary>One shared HttpClient for all profiles: connection pooling,
    /// and crucially no per-request timeout (cold loads and long agent turns
    /// are minutes apart; cancellation travels with the client disconnect).</summary>
    private static readonly Lazy<HttpClient> _gatewayHttp = new(() => new HttpClient
    {
        Timeout = Timeout.InfiniteTimeSpan,
    });

    private static HttpClient GatewayHttp => _gatewayHttp.Value;

    /// <summary>Admin-managed keys are optional (the box runs fine on config
    /// keys alone), so resolve defensively rather than requiring the
    /// registration.</summary>
    private static IAiKeyStore? KeyStore(HttpRequest request) =>
        request.HttpContext.RequestServices.GetService<IAiKeyStore>();

    private static IResult Result(string jsonBody, int statusCode) =>
        Results.Text(jsonBody, "application/json", status: statusCode);

    /// <summary>Writes nothing - used after the response was already streamed.
    /// Belt-and-braces: if the framework ever does execute it against an
    /// unstarted response (e.g. an empty 2xx body edge case), it stays inert
    /// instead of double-writing.</summary>
    private sealed class EmptyResult : IResult
    {
        public static readonly EmptyResult Instance = new();

        public Task ExecuteAsync(HttpContext http) =>
            http.Response.HasStarted ? Task.CompletedTask : Task.CompletedTask;
    }
}
