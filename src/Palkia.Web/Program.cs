using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Palkia.Core.Broker;
using Palkia.Core.Metrics;
using Palkia.Core.Minecraft;
using Palkia.Core.Services;
using Palkia.Data;
using Palkia.Infra.Metrics;
using Palkia.Infra.Minecraft;
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

var idleTimeoutMinutes = builder.Configuration.GetValue("Auth:IdleTimeoutMinutes", 60);

builder.Services.ConfigureApplicationCookie(options =>
{
    options.LoginPath = "/Account/Login";
    options.LogoutPath = "/Account/Logout";
    options.AccessDeniedPath = "/Account/AccessDenied";
    // This alone only covers inactivity across real HTTP requests (new page
    // loads, Razor Page posts). A user idling on an already-open Blazor
    // Server page won't generate any of those - see the client-side idle
    // watcher wired up in _Host.cshtml + wwwroot/js/idle-timeout.js, which
    // is what actually catches that case and forces a sign-out.
    options.ExpireTimeSpan = TimeSpan.FromMinutes(idleTimeoutMinutes);
    options.SlidingExpiration = true;
});

// --- Infra: OpenRC service control -----------------------------------------

builder.Services.AddSingleton(new OpenRcClientOptions
{
    WrapperPath = builder.Configuration["OpenRc:WrapperPath"] ?? "/usr/local/sbin/palkia-rc-ctl",
    SudoPath = builder.Configuration["OpenRc:SudoPath"] ?? "/usr/bin/sudo",
});
builder.Services.AddSingleton<IServiceControlClient, OpenRcServiceClient>();

// --- Core: resource broker --------------------------------------------------
// One shared pool the whole box's heavy services draw leases from. Only
// Minecraft draws from it today; AI/VM/Kdenlive will in later phases, at
// which point this is what stops them all from running at once and taking
// the box down. See Palkia.Core/Broker for the admission-control logic.

var resourceBudget = new ResourceBudget(
    CpuCores: builder.Configuration.GetValue("ResourceBroker:CpuCores", 3),
    MemoryMb: builder.Configuration.GetValue("ResourceBroker:MemoryMb", 12288),
    VramMb: builder.Configuration.GetValue("ResourceBroker:VramMb", 11000));

builder.Services.AddSingleton<IResourceBroker>(new ResourceBroker(resourceBudget));

var minecraftOptions = new MinecraftOptions
{
    CpuCores = builder.Configuration.GetValue("Minecraft:CpuCores", 2),
    MemoryMb = builder.Configuration.GetValue("Minecraft:MemoryMb", 6144),
    IdleShutdownMinutes = builder.Configuration.GetValue("Minecraft:IdleShutdownMinutes", 30),
};
builder.Services.AddSingleton(minecraftOptions);

var consoleOptions = new MinecraftConsoleOptions
{
    LogPath = builder.Configuration["MinecraftConsole:LogPath"] ?? "/var/log/minecraft/minecraft.log",
    ConsoleFifoPath = builder.Configuration["MinecraftConsole:ConsoleFifoPath"] ?? "/var/lib/minecraft/console.fifo",
    CommandWriteTimeoutSeconds = builder.Configuration.GetValue("MinecraftConsole:CommandWriteTimeoutSeconds", 3),
};
builder.Services.AddSingleton(consoleOptions);
builder.Services.AddSingleton<IMinecraftConsoleClient, MinecraftConsoleClient>();
builder.Services.AddHostedService<MinecraftIdleShutdownService>();

// --- Infra: system metrics ---------------------------------------------------
// One shared instance: registered as the concrete type so AddHostedService
// and the ISystemMetricsProvider registration both resolve to the same
// object, rather than the host silently creating two separate samplers.
builder.Services.AddSingleton<LinuxSystemMetricsSampler>();
builder.Services.AddSingleton<ISystemMetricsProvider>(sp => sp.GetRequiredService<LinuxSystemMetricsSampler>());
builder.Services.AddHostedService(sp => sp.GetRequiredService<LinuxSystemMetricsSampler>());

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

    // Defense in depth: if the configured Data Source path's directory
    // doesn't exist (wrong environment picked up, fresh install, whatever),
    // fail with a clear filesystem error instead of SQLite's opaque
    // "Error 14: unable to open database file".
    var dataSource = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder(connectionString).DataSource;
    var dbDirectory = Path.GetDirectoryName(Path.GetFullPath(dataSource));
    if (!string.IsNullOrEmpty(dbDirectory))
    {
        Directory.CreateDirectory(dbDirectory);
    }

    db.Database.EnsureCreated();

    await SeedAdminAsync(scope.ServiceProvider, app.Configuration, app.Logger);
    await ReconcileBrokerAsync(scope.ServiceProvider, app.Logger);
}

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}

app.UseStaticFiles();
app.UseRouting();

app.UseAuthentication();
app.UseAuthorization();

app.MapRazorPages();
app.MapBlazorHub();
app.MapFallbackToPage("/_Host");

app.Run();

static async Task ReconcileBrokerAsync(IServiceProvider services, ILogger logger)
{
    var serviceControl = services.GetRequiredService<IServiceControlClient>();
    var broker = services.GetRequiredService<IResourceBroker>();
    var minecraftOptions = services.GetRequiredService<MinecraftOptions>();

    var minecraft = ManagedServiceCatalog.Find("minecraft");
    if (minecraft is null)
    {
        return;
    }

    var status = await serviceControl.GetStatusAsync(minecraft);
    if (status.State != ServiceRunState.Started)
    {
        return;
    }

    var amount = new ResourceBudget(minecraftOptions.CpuCores, minecraftOptions.MemoryMb, 0);
    broker.AdoptExistingLease(new LeaseRequest(minecraft.Id, minecraft.DisplayName, amount));

    logger.LogInformation(
        "Minecraft was already running at startup - adopted a {CpuCores}-core/{MemoryMb}MB lease " +
        "for it so the broker's accounting matches reality.",
        minecraftOptions.CpuCores, minecraftOptions.MemoryMb);
}

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
