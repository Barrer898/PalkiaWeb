namespace Palkia.Web.Pages;

/// <summary>One bubble in the chat transcript. Failed attempts never become
/// entries - the page removes them and shows the error line instead - so
/// everything in the transcript is safe to replay upstream as history.</summary>
internal sealed class ChatEntry
{
    public string Role { get; init; } = "";
    public string Content { get; set; } = "";

    /// <summary>Profile that produced (or is producing) an assistant entry.</summary>
    public string ProfileId { get; init; } = "";

    /// <summary>True while the request is in flight; rendered as a status line.</summary>
    public bool Pending { get; set; }

    public TimeSpan? Elapsed { get; set; }
}
