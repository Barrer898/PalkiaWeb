using Microsoft.AspNetCore.Identity;

namespace Palkia.Data;

/// <summary>
/// One of up to ~6 household accounts. Kept close to stock IdentityUser on
/// purpose - per-user resource quotas (max VM RAM, AI session minutes, etc.)
/// will be added as their own table once the resource broker (phase 2)
/// defines what a quota actually needs to look like, rather than guessing
/// the shape here.
/// </summary>
public sealed class ApplicationUser : IdentityUser
{
    public string DisplayName { get; set; } = string.Empty;

    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
}
