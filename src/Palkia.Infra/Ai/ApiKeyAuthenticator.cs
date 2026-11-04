using System.Security.Cryptography;
using System.Text;
using Palkia.Core.Ai;

namespace Palkia.Infra.Ai;

/// <summary>
/// Shared-secret auth for the gateway, mirroring FreeToken's own scheme
/// (`Authorization: Bearer &lt;key&gt;` or `x-api-key`, per docs/cli.md) so
/// OpenAI/Anthropic-compatible clients work unchanged. Keys come from the
/// FreeToken:ApiKeys config section - separate credential per agent/user
/// where practical (deployment rules §8). Comparison is constant-time; keys
/// are never logged, only their labels.
/// </summary>
public static class ApiKeyAuthenticator
{
    public static AuthResult Authenticate(FreeTokenOptions options, HttpRequest request)
    {
        var provided = ExtractKey(request);
        if (provided is null)
        {
            return new AuthResult(null, Error("authentication_error",
                "Missing API key. Send 'Authorization: Bearer <key>' or 'x-api-key: <key>'."), 401);
        }

        foreach (var entry in options.ApiKeys)
        {
            if (string.IsNullOrEmpty(entry.Key))
            {
                continue;   // blank placeholder rows in appsettings are inert
            }

            if (FixedTimeEquals(provided, entry.Key))
            {
                return new AuthResult(new AiCaller(
                    string.IsNullOrWhiteSpace(entry.Label) ? "unnamed-key" : entry.Label,
                    entry.AllowedProfiles), null, 0);
            }
        }

        return new AuthResult(null, Error("authentication_error", "Invalid API key."), 401);
    }

    private static string? ExtractKey(HttpRequest request)
    {
        if (request.Headers.TryGetValue("x-api-key", out var header) && !StringValuesAreBlank(header))
        {
            return header.ToString().Trim();
        }

        var auth = request.Headers.Authorization.ToString();
        if (auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            var token = auth["Bearer ".Length..].Trim();
            return token.Length > 0 ? token : null;
        }

        // Also accept ?api_key= - some lightweight clients can't set headers.
        if (request.Query.TryGetValue("api_key", out var query) && !StringValuesAreBlank(query))
        {
            return query.ToString().Trim();
        }

        return null;
    }

    private static bool StringValuesAreBlank(Microsoft.Extensions.Primitives.StringValues values) =>
        values.Count == 0 || string.IsNullOrWhiteSpace(values.ToString());

    private static bool FixedTimeEquals(string a, string b)
    {
        var ba = Encoding.UTF8.GetBytes(a);
        var bb = Encoding.UTF8.GetBytes(b);
        return ba.Length == bb.Length && CryptographicOperations.FixedTimeEquals(ba, bb);
    }

    private static string Error(string type, string message) =>
        System.Text.Json.JsonSerializer.Serialize(new { error = new { type, code = type, message } });
}
