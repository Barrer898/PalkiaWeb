namespace Palkia.Web.Pages;

internal sealed class ChatEntry
{
    public string Role { get; init; } = "";
    public string Content { get; set; } = "";
    internal string _profileId { get; set; } = "";
}
