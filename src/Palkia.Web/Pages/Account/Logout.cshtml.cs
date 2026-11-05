using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Palkia.Data;

namespace Palkia.Web.Pages.Account;

// The sign-out form lives inside MainLayout.razor, a Blazor component, which
// has no access to Razor Pages' @Html.AntiForgeryToken(). Rather than fake up
// a token by hand, we accept a plain POST here: the request still needs a
// valid auth cookie to do anything (SignInManager.SignOutAsync just clears
// it), so the actual attack surface an antiforgery token would close - a
// forged cross-site POST tricking an authenticated browser into signing
// itself out - is a nuisance, not a compromise. Worth revisiting if this
// page ever does more than sign-out.
[IgnoreAntiforgeryToken]
public sealed class LogoutModel : PageModel
{
    private readonly SignInManager<ApplicationUser> _signInManager;

    public LogoutModel(SignInManager<ApplicationUser> signInManager)
    {
        _signInManager = signInManager;
    }

    public async Task<IActionResult> OnPostAsync()
    {
        await _signInManager.SignOutAsync();
        return RedirectToPage();
    }
}
