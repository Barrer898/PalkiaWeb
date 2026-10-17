using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Palkia.Data;

namespace Palkia.Web.Pages.Account;

public sealed class LoginModel : PageModel
{
    private readonly SignInManager<ApplicationUser> _signInManager;

    public LoginModel(SignInManager<ApplicationUser> signInManager)
    {
        _signInManager = signInManager;
    }

    [BindProperty]
    public InputModel Input { get; set; } = new();

    [BindProperty(SupportsGet = true)]
    public string? ReturnUrl { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? Reason { get; set; }

    public string? ErrorMessage { get; private set; }

    public void OnGet()
    {
        if (Reason == "idle")
        {
            ErrorMessage = "You were signed out after a period of inactivity.";
        }
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (!ModelState.IsValid)
        {
            return Page();
        }

        var result = await _signInManager.PasswordSignInAsync(
            Input.Username,
            Input.Password,
            Input.RememberMe,
            lockoutOnFailure: true);

        if (result.Succeeded)
        {
            return LocalRedirect(IsLocalSafe(ReturnUrl) ? ReturnUrl! : "/");
        }

        ErrorMessage = result.IsLockedOut
            ? "Too many failed attempts. Try again in a few minutes."
            : "Invalid username or password.";

        return Page();
    }

    // LocalRedirect() already guards against open redirects, but we check
    // explicitly too since ReturnUrl comes straight from the query string.
    private bool IsLocalSafe(string? url) =>
        !string.IsNullOrWhiteSpace(url) && Url.IsLocalUrl(url);

    public sealed class InputModel
    {
        [Required]
        public string Username { get; set; } = string.Empty;

        [Required]
        public string Password { get; set; } = string.Empty;

        public bool RememberMe { get; set; }
    }
}
