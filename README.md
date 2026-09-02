# Palkia

A homeserver control panel for a single shared box (Gentoo/OpenRC) running
local AI inference, a Minecraft server, VM hosting, Kdenlive rendering, and
SMB/NFS file sharing for up to ~6 accounts. ASP.NET Core + Blazor Server +
SQLite.

The core design problem this app exists to solve isn't "start/stop services"
- it's admission control on genuinely limited hardware (4-thread CPU, 16GB
RAM). See the phased plan below.

## Status: Phase 1

What's here:

- ASP.NET Core Identity (username/password) + SQLite, roles (`Admin`/`User`)
- Blazor Server UI shell (dashboard placeholder, service status page, admin
  user management)
- OpenRC service control for Samba/NFS via a narrow, allow-listed root
  wrapper script (`deploy/palkia-rc-ctl.sh`) - the app itself runs
  unprivileged

Not here yet (later phases):

- Resource broker / lease system (`Palkia.Core/Broker`) - arbitrates
  CPU/RAM/VRAM across services so heavy jobs can't run concurrently and take
  the box down
- Minecraft, AI inference (llama.cpp/FreeToken), Kdenlive render queue, VM
  hosting modules
- TOTP second factor
- Real EF Core Migrations (currently using `EnsureCreated()` - fine for a
  greenfield phase-1 schema, not for evolving it later)

## Project layout

```
src/Palkia.Web     Blazor Server UI, Razor Pages (login/logout), Program.cs
src/Palkia.Core     Domain: ManagedService catalog, IServiceControlClient
src/Palkia.Infra     OpenRcServiceClient - talks to the root wrapper script
src/Palkia.Data     EF Core + Identity, SQLite
deploy/             OpenRC init/conf scripts, the privileged wrapper script
```

## Local development

Requires the .NET 8 SDK (not available in the sandbox this was scaffolded
in, so this hasn't actually been build-tested yet - do a `dotnet build` as
your first step and expect to fix small things).

```sh
cd src/Palkia.Web
dotnet user-secrets set "AdminSeed:Username" "you"
dotnet user-secrets set "AdminSeed:Password" "something-at-least-10-chars"
dotnet run
```

Visit `https://localhost:5001` (or whatever port `dotnet run` prints), sign
in with the seeded admin account. In dev, `OpenRcServiceClient` will try to
call `sudo /usr/local/sbin/palkia-rc-ctl` and fail loudly if that's not set
up on your dev machine - that's expected unless you're developing directly
on the Gentoo box with the wrapper installed (see `deploy/README.md`).

## Deploying

See `deploy/README.md` for the full Gentoo/OpenRC setup (service account,
sudoers rule, init script, reverse proxy).
