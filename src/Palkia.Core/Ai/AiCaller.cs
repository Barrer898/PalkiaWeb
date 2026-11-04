namespace Palkia.Core.Ai;

/// <summary>What the gateway needs to know about an authenticated caller.</summary>
public sealed record AiCaller(string Label, IReadOnlyList<string> AllowedProfileIds)
{
    public bool Allows(ModelProfile profile) =>
        AllowedProfileIds.Count == 0 ||
        AllowedProfileIds.Any(id => string.Equals(id, profile.Id, StringComparison.OrdinalIgnoreCase));
}

public sealed record AuthResult(AiCaller? Caller, string? ErrorBody, int StatusCode)
{
    public bool Succeeded => Caller is not null;
}
