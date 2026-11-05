# Palkia

A homeserver control panel for a single shared box (Gentoo/OpenRC) running
local AI inference, a Minecraft server, VM hosting, Kdenlive rendering, and
SMB/NFS file sharing for up to ~6 accounts. ASP.NET Core + Blazor Server +
SQLite.

The core design problem this app exists to solve isn't "start/stop services"
- it's admission control on genuinely limited hardware (4-thread CPU, 16GB
RAM). See the phased plan below.

## Status: Phase 2

What's here:

- ASP.NET Core Identity (username/password) + SQLite, roles (`Admin`/`User`)
- Idle-timeout auto-logout (default 60 min, `Auth:IdleTimeoutMinutes` in
  appsettings) - both the auth cookie's sliding expiration *and* a
  client-side watcher (`wwwroot/js/idle-timeout.js`), since Blazor Server
  activity over the SignalR circuit doesn't renew the cookie on its own
- OpenRC service control for Samba/NFS via a narrow, allow-listed root
  wrapper script (`deploy/palkia-rc-ctl.sh`) - the app itself runs
  unprivileged
- **Resource broker** (`Palkia.Core/Broker`) - in-memory admission control
  over a shared CPU/RAM/VRAM budget (`ResourceBroker` appsettings section).
  Services request a lease before starting; if the budget doesn't cover it
  right now, the request queues (strict FIFO) and gets granted automatically
  as other leases are released
- **Minecraft module** - the first real consumer of the broker. Starting it
  goes through `RequestLease` first; if granted immediately, the OpenRC
  service starts right away, if queued, it starts automatically the moment
  enough budget frees up. Any household user can start/stop it (not
  admin-gated, unlike Samba/NFS/NFS-client)
- Dashboard now shows live budget/headroom/active leases/queue instead of
  being a placeholder, plus live host metrics (CPU%, RAM used, free swap,
  GPU usage/VRAM via nvidia-smi where available) sampled independently of
  the broker - useful for sanity-checking the broker's accounting against
  what the box is actually doing
- **Minecraft console** (`/minecraft/console`) - live-tails the server log
  and can send commands back in via a named pipe wired up as the process's
  stdin (`deploy/palkia-minecraft.initd`'s `--stdin` flag). Viewing is open
  to anyone who can see the Minecraft page; sending commands is admin-only,
  since arbitrary console commands can op/ban/kick players or change game
  rules - a meaningfully bigger power than just starting/stopping the server

Not here yet (later phases):

- AI inference (llama.cpp/FreeToken), Kdenlive render queue, VM hosting
  modules - each will draw its own lease from the same broker once added
- TOTP second factor
- Real EF Core Migrations (currently using `EnsureCreated()` - the Minecraft
  module didn't change the schema, so this is still fine for now, but won't
  be forever)
- Per-user quotas (e.g. "no more than N hours of AI inference a day") -
  mentioned in earlier design discussion, not needed yet since Minecraft
  alone doesn't have anything worth rationing per-person

## Project layout

```
src/Palkia.Web     Blazor Server UI, Razor Pages (login/logout), Program.cs
src/Palkia.Core     Domain: ManagedService catalog, IServiceControlClient,
                     Broker/ (ResourceBudget, Lease, IResourceBroker)
src/Palkia.Infra     OpenRcServiceClient - talks to the root wrapper script
src/Palkia.Data     EF Core + Identity, SQLite
deploy/             OpenRC init/conf scripts, the privileged wrapper script
```

## Local development

Requires the .NET 10 SDK. (Originally scaffolded targeting .NET 8 LTS, then
moved to 10 since that's what was actually available on the Gentoo box -
see git history. No API-level changes were needed, just TargetFramework and
package version bumps.)

```sh
cd src/Palkia.Web
dotnet user-secrets set "AdminSeed:Username" "you"
dotnet user-secrets set "AdminSeed:Password" "something-at-least-10-chars"
dotnet run
```

`Properties/launchSettings.json` sets `ASPNETCORE_ENVIRONMENT=Development`
for `dotnet run`, which is what makes `appsettings.Development.json`'s local
`palkia.dev.db` path take effect instead of the production
`/var/lib/palkia/palkia.db` path (which won't exist yet on a dev machine).

Visit `http://<box-ip>:5000` (bound to all interfaces, see
`launchSettings.json` - this is a homeserver, tested from other devices on
your LAN, not just the box itself), sign in with the seeded admin account.
In dev, `OpenRcServiceClient` will try to call `sudo /usr/local/sbin/palkia-rc-ctl`
and fail with "a password is required" if that's not set up on your dev
machine - that's expected unless you're developing directly on the Gentoo
box with the wrapper installed and your dev user granted sudo rights to it
(see `deploy/README.md`).

## Deploying

See `deploy/README.md` for the full Gentoo/OpenRC setup (service account,
sudoers rule, init script, reverse proxy).
