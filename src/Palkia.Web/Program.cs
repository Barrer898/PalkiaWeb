using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Palkia.Core.Ai;
using Palkia.Core.Broker;
using Palkia.Core.Metrics;
using Palkia.Core.Minecraft;
using Palkia.Core.Services;
using Palkia.Data;
using Palkia.Infra.Ai;
using Palkia.Infra.Metrics;
using Palkia.Infra.Minecraft;
using Palkia.Infra.OpenRc;
using Palkia.Web.Services;

var builder = WebApplication.CreateBuilder(args);

// --- Data / Identity -------------------------------------------------------

var connectionString = builder.Configuration.GetConnectionString("Default")
    ?? "Data Source=palkia.db";

// Use AddDbContextFactory only — it registers singleton DbContextOptions
// (safe for the singleton IDbContextFactory and DbAiKeyStore) and lets
// request-scoped contexts be created via factory.Create(). A separate
// AddDbContext would register scoped DbContextOptions, which conflicts
// with EF Core's DI validation ("Cannot consume scoped DbContextOptions
// from singleton IDbContextFactory"), AND later causes
// "Cannot resolve scoped IDbContextOptionsConfiguration from root" at
// runtime when EF Core composes options at line 192.
builder.Services.AddDbContextFactory<PalkiaDbContext>(o =>
{
    o.UseSqlite(connectionString);
    o.ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.PendingModelChangesWarning));
});

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

// --- Infra: AI serving (FreeToken) -------------------------------------------
// Five-profile lineup driven through the same broker as Minecraft: leases are
// taken on per-TIER slot ids so "never two large models at once" and "Small +
// Fast may coexist" fall out of the budget arithmetic instead of special
// cases. The gateway (/api/llm/v1/*) is the ONLY path to ft - it binds to
// loopback and Palkia adds the per-key auth/concurrency layer (§6/§8). The
// network-level privacy boundary is Tailscale Serve, not nginx - see
// deploy/TAILSCALE.md.

var freeTokenOptions = new FreeTokenOptions
{
    ProfileConfigDir = builder.Configuration["FreeToken:ProfileConfigDir"] ?? "/etc/palkia/freetoken",
    ModelsDir = builder.Configuration["FreeToken:ModelsDir"] ?? "/var/lib/palkia-models",
    BindHost = builder.Configuration["FreeToken:BindHost"] ?? "127.0.0.1",
    IdleShutdownMinutes = builder.Configuration.GetValue("FreeToken:IdleShutdownMinutes", 15),
    LoadTimeoutSeconds = builder.Configuration.GetValue("FreeToken:LoadTimeoutSeconds", 900),
    AlwaysOnProfiles = builder.Configuration.GetSection("FreeToken:AlwaysOnProfiles").Get<List<string>>() ?? new(),
};
// PR review item 1: these bind from Configuration, not Services - the old
// builder.Services.GetSection(...) didn't even compile.
builder.Configuration.GetSection("FreeToken:ApiKeys").Bind(freeTokenOptions.ApiKeys);
builder.Configuration.GetSection("FreeToken:UpstreamApiKeys").Bind(freeTokenOptions.UpstreamApiKeys);
builder.Services.AddSingleton(freeTokenOptions);

// Item 2: FreeTokenClient's constructor takes an HttpClient, so it must be
// registered through AddHttpClient (the typed-client overload injects a
// pooled, properly-lifecycled client). Health/stats polls are loopback and
// tiny; a short timeout keeps a wedged ft instance from hanging the poll
// loop forever.
builder.Services.AddHttpClient<IFreeTokenClient, FreeTokenClient>(client =>
{
    client.Timeout = TimeSpan.FromSeconds(10);
});

builder.Services.AddSingleton<AiServingManager>();
builder.Services.AddSingleton<IAiServingManager>(sp => sp.GetRequiredService<AiServingManager>());
builder.Services.AddHostedService<AiServingHostedService>();

// Item 7: admin-managed API keys, hashed at rest in SQLite. Registered as a
// singleton because ApiKeyAuthenticator reads its in-memory snapshot on the
// request hot path; LoadAsync() below fills that snapshot BEFORE the gateway
// is mapped (item: initialized before accepting requests).
builder.Services.AddSingleton<IAiKeyStore, DbAiKeyStore>();

// Website chat client — talks to the same loopback gateway as external agents.
// Registered as scoped so each Blazor circuit gets its own instance.
// Uses a single shared HttpClient since it only calls internal endpoints.
// The port defaults to 5000 (the default Kestrel port) or can be overridden
// via the "Chat:BaseUrl" config key (e.g. "http://localhost:5000").
var chatBaseUrl = builder.Configuration.GetValue("Chat:BaseUrl", "http://localhost:5000");

builder.Services.AddScoped<IWebsiteChatService, WebsiteChatService>(sp =>
{
    var http = new HttpClient { BaseAddress = new Uri(chatBaseUrl), Timeout = Timeout.InfiniteTimeSpan };
    var config = sp.GetRequiredService<IConfiguration>();
    return new WebsiteChatService(http, config);
});

// --- Web framework -----------------------------------------------------------

builder.Services.AddRazorPages();
builder.Services.AddServerSideBlazor();
builder.Services.AddCascadingAuthenticationState();
builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("AdminOnly", policy => policy.RequireRole("Admin"));
});

// Item 4: Kestrel enforces the SAME body ceiling as the gateway, at the
// connection level - a chunked upload that never declares Content-Length
// gets aborted by the server itself once it crosses the limit, instead of
// being buffered indefinitely before any handler ever sees it. This must run
// on `builder` BEFORE builder.Build(); after Build() the service collection
// is read-only and ConfigureKestrel throws.
builder.WebHost.ConfigureKestrel(kestrel =>
    kestrel.Limits.MaxRequestBodySize = AiGatewayEndpoints.MaxRequestBodyBytes);

var app = builder.Build();

// --- Dev-time DB bring-up + first-run admin seed ----------------------------
// Item 8: real EF Core migrations, not EnsureCreated(). EnsureCreated never
// upgrades an existing database, so the new AiApiKeys table would simply be
// missing on every already-installed Palkia. Migrate() applies pending
// migrations on startup (and creates the DB from scratch on a fresh one).
using (var scope = app.Services.CreateScope())
{
    var dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<PalkiaDbContext>>();
    var db = await dbFactory.CreateDbContextAsync();

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

    db.Database.Migrate();

    await SeedAdminAsync(scope.ServiceProvider, app.Configuration, app.Logger);

    await ReconcileBrokerAsync(scope.ServiceProvider, app.Logger);
}

// Item 1/7: fill the hashed-key auth snapshot BEFORE the gateway can accept
// a single request - this runs after the migration block above (the table
// must exist) and long before app.Run() below starts listening.
await app.Services.GetRequiredService<IAiKeyStore>().LoadAsync();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}

app.UseStaticFiles();
app.UseRouting();

app.UseAuthentication();
app.UseAuthorization();

// The LLM gateway is authenticated by its OWN API-key layer (per-agent
// credentials, deployment rules §8), not by the Identity cookie - coding
// agents don't do interactive logins. Reachability is bounded one level down
// instead: Palkia listens on loopback/LAN only and Tailscale Serve is the
// sole remote entry point (deploy/TAILSCALE.md). Map BEFORE the Blazor
// fallback so /api/llm/* never falls through to _Host.
app.MapAiGateway();

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
    if (minecraft is not null)
    {
        var status = await serviceControl.GetStatusAsync(minecraft);
        if (status.State == ServiceRunState.Started)
        {
            var amount = new ResourceBudget(minecraftOptions.CpuCores, minecraftOptions.MemoryMb, 0);
            broker.AdoptExistingLease(new LeaseRequest(minecraft.Id, minecraft.DisplayName, amount));

            logger.LogInformation(
                "Minecraft was already running at startup - adopted a {CpuCores}-core/{MemoryMb}MB lease " +
                "for it so the broker's accounting matches reality.",
                minecraftOptions.CpuCores, minecraftOptions.MemoryMb);
        }
    }

    // ft servers that survived an app restart get re-adopted the same way -
    // AiServingManager checks all five profiles against OpenRC and takes over
    // their tier-slot leases. Idempotent: the hosted service runs it again on
    // start and skips slots whose session already holds a live lease.
    await services.GetRequiredService<AiServingManager>().ReconcileAtStartupAsync();
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
