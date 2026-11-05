using System.Net.Http.Headers;
using System.Text.Json;
using Palkia.Core.Ai;

namespace Palkia.Web.Services;

/// <summary>
/// Server-side chat client that talks to the same AI gateway as external agents.
/// The caller's identity is the ASP.NET Identity cookie — no separate API key
/// is needed for website users, so this uses a "website" key configured in
/// FreeToken:ApiKeys for the website API key.
/// </summary>
public class WebsiteChatService : IWebsiteChatService
{
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
        var sw = System.Diagnostics.Stopwatch.StartNew();

        // Look up a configured website API key so the gateway treats the
        // request as authenticated and tracks it against the caller label.
        string? apiKey = _config["FreeToken:WebsiteApiKey"]
            ?? _config["FreeToken:ApiKeys:website"]
            ?? _config["FreeToken:ApiKeys:chat"];

        var request = new
        {
            model = profileId,
            messages = history.Select(m => new { m.Role, m.Content }).ToArray(),
            stream = true,
            max_tokens = 4096,
        };

        var body = new System.Net.Http.StringContent(
            JsonSerializer.Serialize(request));
        body.Headers.ContentType = new MediaTypeHeaderValue("application/json")
        {
            CharSet = "utf-8",
        };

        // Attach the website API key as a Bearer token so the gateway
        // treats the request as authenticated.
        if (!string.IsNullOrEmpty(apiKey))
        {
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        }

        try
        {
            using var response = await _http.PostAsync(
                "/api/llm/v1/chat/completions", body, ct);

            // If the gateway returns a non-streaming error (e.g. 401, 429, 502),
            // read the body and return it as an error.
            if (!response.IsSuccessStatusCode)
            {
                var errBody = await response.Content.ReadAsStringAsync(ct);
                string errMsg;
                try
                {
                    var errJson = JsonDocument.Parse(errBody);
                    errMsg = errJson.RootElement
                        .GetProperty("error")
                        .GetProperty("message")
                        .GetString() ?? errBody;
                }
                catch
                {
                    errMsg = errBody;
                }
                return ChatReply.Fail(errMsg, (int)response.StatusCode, profileId, sw.Elapsed);
            }

            // Stream the SSE response to collect the final reply.
            using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var reader = new StreamReader(stream);

            var replyBuilder = new System.Text.StringBuilder();
            string? line;
            while ((line = await reader.ReadLineAsync()) != null && !ct.IsCancellationRequested)
            {
                if (!line.StartsWith("data: ", StringComparison.Ordinal))
                    continue;

                var data = line.Substring(6);
                if (data.Contains("\"finish_reason\""))
                    continue; // done — no content in the final chunk

                try
                {
                    using var chunk = JsonDocument.Parse(data);
                    var delta = chunk.RootElement
                        .GetProperty("choices")[0]
                        .GetProperty("delta")
                        .GetProperty("content")
                        .GetString();
                    if (!string.IsNullOrEmpty(delta))
                        replyBuilder.Append(delta);
                }
                catch
                {
                    // Ignore malformed SSE chunks.
                }
            }

            return ChatReply.Ok(replyBuilder.ToString(), profileId, sw.Elapsed);
        }
        catch (OperationCanceledException)
        {
            return ChatReply.Fail("Request cancelled.", 499, profileId, sw.Elapsed);
        }
        catch (HttpRequestException ex)
        {
            return ChatReply.Fail(ex.Message, 502, profileId, sw.Elapsed);
        }
    }
}
