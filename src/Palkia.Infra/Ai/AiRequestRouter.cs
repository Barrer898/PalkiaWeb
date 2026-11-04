using System.Text.Json;
using Palkia.Core.Ai;

namespace Palkia.Infra.Ai;

/// <summary>
/// The single place that turns "a client wants to talk to a model" into
/// "which profile, with what admission decision". Used by both the HTTP
/// gateway endpoint (agents) and the Blazor chat page (household humans),
/// so the website is one consumer of the same brokered path rather than a
/// privileged side door. Pure/static - no injected state - so it stays
/// trivially testable.
/// </summary>
public static class AiRequestRouter
{
    /// <summary>Max bytes buffered for a non-streaming response copy. A
    /// chat-completions JSON answer never realistically approaches this;
    /// anything above it indicates something wrong upstream.</summary>
    public const int MaxBufferedResponseBytes = 64 * 1024 * 1024;

    private static readonly string[] AllowedForwardHeaders =
    [
        "content-type",
        "accept",
        "anthropic-version",   // passthrough for /v1/messages clients
    ];

    public static ModelProfile? ResolveProfile(string? requestedModel)
    {
        if (string.IsNullOrWhiteSpace(requestedModel))
        {
            return null;
        }

        // Accept the served name, the catalog id, or tier aliases so agent
        // configs can say "palkia-fast" instead of the exact checkpoint name.
        return ModelProfileCatalog.All.FirstOrDefault(x =>
            string.Equals(x.ApiName, requestedModel, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(x.Id, requestedModel, StringComparison.OrdinalIgnoreCase) ||
            string.Equals($"palkia-{x.Id}", requestedModel, StringComparison.OrdinalIgnoreCase) ||
            string.Equals($"palkia-{x.TierLabel.ToLowerInvariant()}", requestedModel, StringComparison.OrdinalIgnoreCase));
    }

    public static string BuildUpstreamUrl(FreeTokenOptions options, ModelProfile profile, string restPath, string queryString)
    {
        var url = $"http://{options.BindHost}:{profile.Port}/v1/{restPath}";
        return string.IsNullOrEmpty(queryString) ? url : $"{url}?{queryString}";
    }

    public static void CopyRequestHeaders(HttpRequest request, HttpRequestMessage target)
    {
        foreach (var header in request.Headers)
        {
            if (Array.IndexOf(AllowedForwardHeaders, header.Key.ToLowerInvariant()) >= 0)
            {
                target.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray());
            }
        }
    }

    public static HttpResponseMessage BuildErrorResponse(int statusCode, string jsonBody)
    {
        return new HttpResponseMessage((System.Net.HttpStatusCode)statusCode)
        {
            Content = new StringContent(jsonBody, Utf8NoBom),
        };
    }

    /// <summary>Copy an upstream response onto the wire. Streaming responses
    /// (SSE / chunked JSON-lines) are piped through byte-for-byte without
    /// buffering - the whole point of not re-parsing them. Non-stream ones
    /// are capped against a ceiling so a runaway completion can't exhaust
    /// the app's memory.</summary>
    public static async Task CopyResponseAsync(HttpResponseMessage upstream, HttpContext http, CancellationToken ct)
    {
        http.Response.StatusCode = (int)upstream.StatusCode;
        http.Response.ContentType = upstream.Content.Headers.ContentType?.ToString() ?? "application/json";

        if (upstream.Headers.TryGetValue("retry-after", out var retryAfter))
        {
            http.Response.Headers["Retry-After"] = retryAfter.ToArray();
        }

        await using var upstreamStream = await upstream.Content.ReadAsStreamAsync(ct);
        var buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(8192);
        try
        {
            long total = 0;
            int read;
            while ((read = await upstreamStream.ReadAsync(buffer.AsMemory(), ct)) > 0)
            {
                total += read;
                if (!IsStreaming(http) && total > MaxBufferedResponseBytes)
                {
                    break;   // defensive ceiling on buffered (non-SSE) payloads
                }
                await http.Response.Body.WriteAsync(buffer.AsMemory(0, read), ct);
            }
        }
        finally
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static bool IsStreaming(HttpContext http) =>
        http.Response.ContentType?.Contains("event-stream", StringComparison.OrdinalIgnoreCase) == true;

    public static string OpenAiError(string type, string message) =>
        JsonSerializer.Serialize(new { error = new { type, code = type, message } });

    public static string AnthropicError(string type, string message) =>
        JsonSerializer.Serialize(new { type = "error", error = new { type, message } });

    /// <summary>Read the "model" field out of a JSON request body without
    /// deserializing the whole thing (bodies can be 100K-token prompts).</summary>
    public static async Task<string?> PeekRequestedModelAsync(Stream body, CancellationToken ct)
    {
        using var doc = await JsonDocument.ParseAsync(body, new JsonDocumentOptions { MaxDepth = 12 }, ct);

        if (doc.RootElement.ValueKind == JsonValueKind.Object &&
            doc.RootElement.TryGetProperty("model", out var model) &&
            model.ValueKind == JsonValueKind.String)
        {
            return model.GetString();
        }

        return null;
    }

    internal static readonly System.Text.Encoding Utf8NoBom =
        new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
}
