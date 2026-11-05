namespace Palkia.Core.Ai;

/// <summary>One visible message in the website chat. Role matches the OpenAI
/// wire format so history can be replayed upstream without translation.</summary>
public sealed record ChatMessage(string Role, string Content);

/// <summary>Outcome of a chat completion through the brokered path. Exactly
/// one of Reply/Error is set; Status mirrors the HTTP code the same failure
/// would produce on the gateway (409 admission denied, 503 still loading,
/// 502 upstream down) so the UI can phrase things honestly.</summary>
public sealed record ChatReply(
    bool Succeeded,
    string? Reply,
    string? Error,
    int Status,
    string ProfileId,
    TimeSpan Elapsed)
{
    public static ChatReply Ok(string reply, string profileId, TimeSpan elapsed) =>
        new(true, reply, null, 200, profileId, elapsed);

    public static ChatReply Fail(string error, int status, string profileId, TimeSpan elapsed) =>
        new(false, null, error, status, profileId, elapsed);
}

/// <summary>
/// The website's own chat client - the household-human counterpart of the
/// external-agent gateway. It goes through the SAME IAiServingManager
/// admission (tier lease + per-caller concurrency + bounded cold-load wait)
/// and talks to the same loopback ft instance; there is deliberately no side
/// door around the resource broker just because the request came from Blazor.
/// Implemented in Palkia.Web (needs Identity for the per-user caller label).
/// </summary>
public interface IWebsiteChatService
{
    Task<ChatReply> CompleteAsync(
        string callerLabel,
        string profileId,
        IReadOnlyList<ChatMessage> history,
        CancellationToken ct = default);
}
