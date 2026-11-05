using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Palkia.Core.Ai;

namespace Palkia.Infra.Ai;

/// <summary>
/// The OpenAI/Anthropic-compatible gateway every external client (coding
/// agents on the tailnet) and the website chat page go through. Mounted at
/// /api/llm - see deploy/TAILSCALE.md for why the public side of this is
/// Tailscale Serve, not nginx or a port opened to the internet.
///
/// Pipeline per request (PR review item 3 fixes the ordering):
///   1. API-key auth - header only: Authorization: Bearer or x-api-key
///      (item 6: no query-parameter keys, ever);
///   2. size-capped body read + resolve "model" -> profile;
///   3. check the caller's AllowedProfiles BEFORE any admission side effect;
///   4. admission through IAiServingManager (per-key permit -> tier lease ->
///      bounded cold-load wait) - an unauthorized key never starts a model,
///      never holds a lease, never takes a queue position;
///   5. replay the EXACT inbound bytes to ft's /v1/* route and pipe the
///      upstream response back byte-for-byte - SSE is never re-parsed.
/// </summary>
public static class AiGatewayEndpoints
{
    /// <summary>Hard ceiling on a single inbound request body. A 110K-token
    /// agent prompt with tool schemas is well under this; anything bigger is
    /// either a mistake or somebody aiming at the box's RAM. Enforced while
    /// STREAMING the body in (item 4), not after buffering it, and mirrored
    /// on Kestrel (Program.cs sets MaxRequestBodySize to the same value).</summary>
    public const long MaxRequestBodyBytes = 64L * 1048576;

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

        // OpenAI surface. The handler returns IResult so MinimalApis writes it
        // to the response; we bind with an explicit non-generic Delegate cast
        // because MapPost(RequestDelegate) would discard the IResult (ASP0016).
        group.MapPost("/v1/chat/completions", (Delegate)(Func<HttpContext, Task<IResult>>)(http => ProxyAsync(http, "chat/completions")));
        group.MapPost("/v1/completions", (Delegate)(Func<HttpContext, Task<IResult>>)(http => ProxyAsync(http, "completions")));
        group.MapPost("/v1/embeddings", (Delegate)(Func<HttpContext, Task<IResult>>)(http => ProxyAsync(http, "embeddings")));

        // Anthropic surface (Claude Code et al. point at /v1/messages).
        group.MapPost("/v1/messages", (Delegate)(Func<HttpContext, Task<IResult>>)(http => ProxyAsync(http, "messages")));
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

        if (http.Request.ContentLength > MaxRequestBodyBytes)
        {
            return Result(AiRequestRouter.OpenAiError("invalid_request",
                $"Request body exceeds the {MaxRequestBodyBytes / 1048576} MiB limit."), 413);
        }

        // Item 4: cap WHILE reading. Aborts as soon as the running total
        // crosses the ceiling, so an unbounded chunked upload gets cut off
        // instead of being fully buffered first.
        using var buffered = new MemoryStream();
        try
        {
            await CopyToAsyncWithLimitAsync(http.Request.Body, buffered, MaxRequestBodyBytes, ct);
        }
        catch (BodyTooLargeException)
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

        // Items 3 + 6 + 7 all happen inside AdmitRequestAsync, in that order:
        // resolve model -> AllowedProfiles check -> per-key permit (stable
        // AiKeyId, global policy) -> ONLY THEN lease/start/queue.
        var admission = await manager.AdmitRequestAsync(caller, requestedModel, DefaultMaxWaitMs, ct);
        using (admission)   // idempotent; also hooked to ResponseCompleted below for stream-exact release
        {
            if (admission.Profile is null)
            {
                if (admission.RetryAfterSeconds > 0)
                {
                    http.Response.Headers.RetryAfter = admission.RetryAfterSeconds.ToString();
                }

                return Result(admission.ErrorBody!, admission.StatusCode);
            }

            var profile = admission.Profile;
            var target = AiRequestRouter.BuildUpstreamUrl(options, profile, restPath,
                http.Request.QueryString.HasValue ? http.Request.QueryString.Value![1..] : "");

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
                // Release the per-key permit exactly when the response has
                // fully left the wire - including streaming completions and
                // client disconnects mid-stream. OnResponseCompleted fires
                // after Kestrel finishes flushing this response (success,
                // abort or disconnect alike); Dispose is idempotent, so the
                // outer `using (admission)` is just the fallback for the
                // never-started edge cases (early 4xx/5xx IResult paths).
                http.Response.OnCompleted(() => { admission.Dispose(); return Task.CompletedTask; });
                await AiRequestRouter.CopyResponseAsync(upstream, http, ct);
            }
        }

        return EmptyResult.Instance;
    }

    /// <summary>Stream-copy with a hard running-total ceiling. Works for
    /// chunked bodies (no Content-Length) because it never trusts the
    /// headers - only bytes actually read.</summary>
    internal static async Task CopyToAsyncWithLimitAsync(Stream source, MemoryStream target, long limit, CancellationToken ct)
    {
        var buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(8192);
        try
        {
            int read;
            long total = 0;
            while ((read = await source.ReadAsync(buffer.AsMemory(), ct)) > 0)
            {
                total += read;
                if (total > limit)
                {
                    throw new BodyTooLargeException();
                }

                target.Write(buffer, 0, read);
            }
        }
        finally
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private sealed class BodyTooLargeException : Exception;

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
        Results.Json(JsonDocument.Parse(jsonBody).RootElement, statusCode: statusCode);

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
