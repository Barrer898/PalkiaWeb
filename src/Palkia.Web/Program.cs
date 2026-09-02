using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Palkia.Core.Services;
using Palkia.Data;
using Palkia.Infra.OpenRc;

var builder = WebApplication.CreateBuilder(args);

// --- Data / Identity -------------------------------------------------------

var connectionString = builder.Configuration.GetConnectionString("Default")
    ?? "Data Source=palkia.db";

builder.Services.AddDbContext<PalkiaDbContext>(options =>
    options.UseSqlite(connectionString));

builder.Services.AddIdentity<ApplicationUser, IdentityRole>(options =>
    {
        // No self-registration in this app - accounts are created by an
        // admin from the Users page - so password policy is set by us,
        // not "what a random signup form should nag about".
        options.Password.RequiredLength = 10;
        options.Password.RequireNonAlphanumeric = false;
        options.Password.RequireUppercase = false;
        options.SignIn.RequireConfirmedAccount = false;
        options.User.RequireUniqueEmail = false;
        options.Lockout.MaxFailedAccessAttempts = 5;
        options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(10);
    })
    .AddEntityFrameworkStores<PalkiaDbContext>()
    .AddDefaultTokenProviders();

builder.Services.ConfigureApplicationCookie(options =>
{
    options.LoginPath = "/Account/Login";
    options.LogoutPath = "/Account/Logout";
    options.AccessDeniedPath = "/Account/AccessDenied";
    options.ExpireTimeSpan = TimeSpan.FromDays(14);
    options.SlidingExpiration = true;
});

// --- Infra: OpenRC service control -----------------------------------------

builder.Services.AddSingleton(new OpenRcClientOptions
{
    WrapperPath = builder.Configuration["OpenRc:WrapperPath"] ?? "/usr/local/sbin/palkia-rc-ctl",
    SudoPath = builder.Configuration["OpenRc:SudoPath"] ?? "/usr/bin/sudo",
});
builder.Services.AddSingleton<IServiceControlClient, OpenRcServiceClient>();

// --- Web framework -----------------------------------------------------------

builder.Services.AddRazorPages();
builder.Services.AddServerSideBlazor();
builder.Services.AddCascadingAuthenticationState();
builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("AdminOnly", policy => policy.RequireRole("Admin"));
});

var app = builder.Build();

// --- Dev-time DB bring-up + first-run admin seed ----------------------------
// EnsureCreated() is a deliberate phase-1 shortcut so we don't need the
// dotnet-ef CLI (not available in the sandbox this was scaffolded in).
// Before phase 2 introduces schema changes, switch this to real EF Core
// Migrations (`dotnet ef migrations add InitialCreate`) so upgrades don't
// require dropping the database.
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<PalkiaDbContext>();
    db.Database.EnsureCreated();

    await SeedAdminAsync(scope.ServiceProvider, app.Configuration, app.Logger);
}

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseRouting();

app.UseAuthentication();
app.UseAuthorization();

app.MapRazorPages();
app.MapBlazorHub();
app.MapFallbackToPage("/_Host");

app.Run();

static async Task SeedAdminAsync(IServiceProvider services, IConfiguration config, ILogger logger)
{
    var userManager = services.GetRequiredService<UserManager<ApplicationUser>>();

    if (userManager.Users.Any())
    {
        return; // Not a first run - never touch existing accounts here.
    }

    var username = config["AdminSeed:Username"];
    var password = config["AdminSeed:Password"];

    if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
    {
        logger.LogWarning(
            "No users exist yet and AdminSeed:Username/Password are not configured. " +
            "Set them (e.g. via environment variables or user-secrets) and restart " +
            "to create the first admin account.");
        return;
    }

    var admin = new ApplicationUser
    {
        UserName = username,
        DisplayName = config["AdminSeed:DisplayName"] ?? username,
        EmailConfirmed = false,
    };

    var result = await userManager.CreateAsync(admin, password);
    if (result.Succeeded)
    {
        await userManager.AddToRoleAsync(admin, "Admin");
        logger.LogWarning(
            "Seeded first admin account '{Username}'. Change its password after " +
            "logging in, then remove AdminSeed:Password from configuration.", username);
    }
    else
    {
        logger.LogError(
            "Failed to seed admin account: {Errors}",
            string.Join("; ", result.Errors.Select(e => e.Description)));
    }
}
