using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Palkia.Core.Ai;

namespace Palkia.Web.Services;

/// <summary>
/// Server-side chat client that talks to the same AI gateway as external agents.
/// The website authenticates to the gateway with its own key
/// (FreeToken:WebsiteApiKey), which must ALSO be listed in FreeToken:ApiKeys
/// (or the key store) or the gateway will reject it - see docs/AI-SETUP.md.
/// </summary>
public sealed class WebsiteChatService : IWebsiteChatService, IDisposable
{
    // The gateway itself will wait up to 15 minutes for a cold model, and a big
    // model can then take a while to answer. HttpClient.Timeout is infinite
    // (see Program.cs) so THIS is the real cap on one request.
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromMinutes(25);

    private readonly HttpClient _http;
    private readonly IConfiguration _config;

    public WebsiteChatService(HttpClient http, IConfiguration config)
    {
        _http = http;
        _config = config;
    }

    public async Task<ChatReply> CompleteAsync(
        string callerLabel,
        string profileId,
        IReadOnlyList<ChatMessage> history,
        CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var apiKey = _config["FreeToken:WebsiteApiKey"];

        // OpenAI wire format: lowercase "role"/"content". (Serializing the
        // ChatMessage record's own property names would send "Role"/"Content",
        // which an OpenAI-style server rejects.)
        var payload = new
        {
            model = profileId,
            messages = history.Select(m => new { role = m.Role, content = m.Content }).ToArray(),
            stream = true,
            max_tokens = 4096,
        };

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(RequestTimeout);

        // Per-request message, so the auth header never lands on shared
        // client state.
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/llm/v1/chat/completions")
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"),
        };
        if (!string.IsNullOrEmpty(apiKey))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        }

        try
        {
            // ResponseHeadersRead: return as soon as headers arrive instead of
            // buffering the whole (streamed) body first.
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);

            if (!response.IsSuccessStatusCode)
            {
                var errBody = await response.Content.ReadAsStringAsync(cts.Token);
                return ChatReply.Fail(ExtractErrorMessage(errBody), (int)response.StatusCode, profileId, sw.Elapsed);
            }

            var isSse = string.Equals(
                response.Content.Headers.ContentType?.MediaType, "text/event-stream",
                StringComparison.OrdinalIgnoreCase);

            if (!isSse)
            {
                // Server ignored stream=true and sent one JSON document.
                var json = await response.Content.ReadAsStringAsync(cts.Token);
                return ChatReply.Ok(ExtractMessageContent(json), profileId, sw.Elapsed);
            }

            using var stream = await response.Content.ReadAsStreamAsync(cts.Token);
            using var reader = new StreamReader(stream);

            var reply = new StringBuilder();
            while (await reader.ReadLineAsync(cts.Token) is { } line)
            {
                if (!line.StartsWith("data:", StringComparison.Ordinal))
                {
                    continue;
                }

                var data = line.Substring(5).Trim();
                if (data.Length == 0)
                {
                    continue;
                }

                if (data == "[DONE]")
                {
                    break;
                }

                // Every chunk is parsed, including ones that carry
                // "finish_reason": null - OpenAI-style servers put that field
                // on every chunk, so its presence says nothing about the end.
                var piece = ExtractDeltaContent(data);
                if (!string.IsNullOrEmpty(piece))
                {
                    reply.Append(piece);
                }
            }

            return ChatReply.Ok(reply.ToString(), profileId, sw.Elapsed);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return ChatReply.Fail("Request cancelled.", 499, profileId, sw.Elapsed);
        }
        catch (OperationCanceledException)
        {
            return ChatReply.Fail("Timed out waiting for the model.", 504, profileId, sw.Elapsed);
        }
        catch (HttpRequestException ex)
        {
            return ChatReply.Fail(ex.Message, 502, profileId, sw.Elapsed);
        }
    }

    private static string ExtractErrorMessage(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("error", out var error)
                && error.ValueKind == JsonValueKind.Object
                && error.TryGetProperty("message", out var message)
                && message.ValueKind == JsonValueKind.String)
            {
                return message.GetString() ?? body;
            }
        }
        catch (JsonException)
        {
        }

        return body;
    }

    private static string? ExtractDeltaContent(string data)
    {
        try
        {
            using var chunk = JsonDocument.Parse(data);
            if (chunk.RootElement.TryGetProperty("choices", out var choices)
                && choices.ValueKind == JsonValueKind.Array
                && choices.GetArrayLength() > 0
                && choices[0].TryGetProperty("delta", out var delta)
                && delta.TryGetProperty("content", out var content)
                && content.ValueKind == JsonValueKind.String)
            {
                return content.GetString();
            }
        }
        catch (JsonException)
        {
            // Ignore a malformed chunk rather than losing the whole reply.
        }

        return null;
    }

    private static string ExtractMessageContent(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("choices", out var choices)
                && choices.ValueKind == JsonValueKind.Array
                && choices.GetArrayLength() > 0
                && choices[0].TryGetProperty("message", out var message)
                && message.TryGetProperty("content", out var content)
                && content.ValueKind == JsonValueKind.String)
            {
                return content.GetString() ?? "";
            }
        }
        catch (JsonException)
        {
        }

        return "";
    }

    // The HttpClient is created per instance in Program.cs and owned here;
    // without this every Blazor circuit leaked one.
    public void Dispose() => _http.Dispose();
}
