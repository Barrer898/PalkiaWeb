# Deploying Palkia on Gentoo (OpenRC)

These steps assume you're building/publishing on the same box (or copying a
published output over). Most of this has now been exercised on a real
Gentoo/OpenRC host, but the clean-install path (dedicated `palkia` service
account, reverse proxy) is less well worn than the dev path, so expect to
fix small things.

**Root-owned files never update themselves.** The wrapper, both init scripts
and the launcher are installed copies - `git pull` doesn't touch them, and a
stale one produces confusing errors ("not in the allow-list", a console that
hangs). Run `sh deploy/check-installed.sh` after any pull that touches
`deploy/` (see step 9).

## 1. Install the .NET SDK

```sh
emerge --ask dev-dotnet/dotnet-sdk-bin:10
```

(Check `eselect dotnet list` if you have multiple SDKs installed and need to
select one.)

## 2. Create the service account

```sh
useradd -r -m -d /var/lib/palkia -s /sbin/nologin palkia
mkdir -p /opt/palkia /var/log/palkia /var/lib/palkia
chown palkia:palkia /opt/palkia /var/log/palkia /var/lib/palkia
```

## 3. Build and publish

From the repo root:

```sh
dotnet publish src/Palkia.Web/Palkia.Web.csproj -c Release -o /opt/palkia
```

This runs `PalkiaDbContext.Database.EnsureCreated()` on first start, so no
separate migration step is needed yet. Nothing since phase 1 has changed the
schema, but switch to real EF Core Migrations (see the comment in
`Program.cs`) the first time something does.

## 4. Install the privilege wrapper

```sh
install -o root -g root -m 0755 deploy/palkia-rc-ctl.sh /usr/local/sbin/palkia-rc-ctl
visudo -f /etc/sudoers.d/palkia
```

Contents of `/etc/sudoers.d/palkia`:

```
palkia ALL=(root) NOPASSWD: /usr/local/sbin/palkia-rc-ctl
```

Sanity check as the palkia user before relying on it:

```sh
su - palkia -s /bin/sh -c 'sudo -n /usr/local/sbin/palkia-rc-ctl samba status'
```

## 5. Install the OpenRC service

```sh
install -o root -g root -m 0755 deploy/palkia-web.initd /etc/init.d/palkia-web
install -o root -g root -m 0640 deploy/palkia-web.confd /etc/conf.d/palkia-web
```

Edit `/etc/conf.d/palkia-web` and set `AdminSeed__Username` /
`AdminSeed__Password` before the first start - that's how the first admin
account gets created. Remove them again once you've logged in and changed
the password.

```sh
rc-update add palkia-web default
rc-service palkia-web start
rc-service palkia-web status
tail -f /var/log/palkia/palkia-web.log
```

## 6. Front it with a reverse proxy

The service binds to `127.0.0.1:5080` only (see `palkia-web.confd`). Put
nginx (or your reverse proxy of choice) in front for LAN access, and add TLS
if it'll ever be reachable outside your LAN - this app is handling real
login credentials for real people.

## 7. Confirm the allow-lists agree

`Palkia.Core.Services.ManagedServiceCatalog` (in code) and the
`ALLOWED_SERVICES` list in `deploy/palkia-rc-ctl.sh` (on disk, root-owned)
must list the same OpenRC service names. If you rename or add a service in
one, update the other - the wrapper script is intentionally the source of
truth for what's actually allowed to run as root, so a code-only change
does nothing without it.

## 8. Minecraft

The server runs as its own `minecraft` user, separate from `palkia`, and is
only ever started through the app (which takes a broker lease first) - it is
deliberately **not** added to a runlevel.

```sh
useradd -r -m -d /var/lib/minecraft -s /sbin/nologin minecraft
mkdir -p /var/lib/minecraft /var/log/minecraft
chown minecraft:minecraft /var/lib/minecraft /var/log/minecraft
```

Download a Paper jar to `/var/lib/minecraft/paper.jar` (owned by `minecraft`)
from [papermc.io/downloads](https://papermc.io/downloads) and accept Mojang's
EULA yourself - the init script refuses to start without it:

```sh
echo "eula=true" > /var/lib/minecraft/eula.txt
chown minecraft:minecraft /var/lib/minecraft/eula.txt
```

Install **three** files. The target names matter: `ManagedServiceCatalog` and
the wrapper's allow-list both expect the service to be called `minecraft`,
not `palkia-minecraft`.

```sh
install -o root -g root -m 0755 deploy/palkia-minecraft-launch.sh /usr/local/sbin/palkia-minecraft-launch
install -o root -g root -m 0755 deploy/palkia-minecraft.initd     /etc/init.d/minecraft
install -o root -g root -m 0640 deploy/palkia-minecraft.confd     /etc/conf.d/minecraft
```

Three numbers have to agree **by hand** - none is derived from another:
`minecraft_heap` (and `memory.max` in `rc_cgroup_settings`) in
`/etc/conf.d/minecraft`, and `Minecraft:MemoryMb` in the app's config. The
broker's lease only decides *when* the server may start; the cgroup is what
actually caps it while running.

### Console access

The app sends commands through a named pipe (`console.fifo`) that is the
server's stdin, and shows output by tailing `minecraft.log`.

Why there's a launcher script: `supervise-daemon --stdin` is **not** usable
for this. It opens the pipe read-only before starting java, so startup hangs
until something writes to it (and whatever is written first is read before
the world exists, then crashes with a NullPointerException), and java sees
end-of-file as soon as the first writer closes, so only the first command
ever works. `palkia-minecraft-launch` instead opens the pipe read-**write**
as java's stdin: nothing blocks at startup, and java holds its own write end
so the pipe never reports EOF.

The app user needs to read the log and write the pipe without root:

```sh
usermod -aG minecraft palkia      # whichever user runs the app
```

Group membership only applies to *new* processes - restart the app after
adding it. Verify with the server running and `Done` showing in the log:

```sh
echo "say console works" > /var/lib/minecraft/console.fifo
tail -n 3 /var/log/minecraft/minecraft.log
```

If it doesn't, run `sh deploy/diagnose-console.sh` as your normal user (not
under sudo) and read it top to bottom: it compares installed files to the
repo, shows what java's stdin actually is (`fd/0` must point at
`console.fifo`), and does two timed write tests - the second catches the
"only the first command works" failure. If the log can't be read, the log
file's own mode may be stricter than its directory allows: `chmod 640
/var/log/minecraft/minecraft.log`.

Commands can't be sent until the log shows `Done (...)! For help, type
"help"`; the app refuses earlier ones (see `IsServerReadyAsync`), because a
command read during startup crashes with the NPE above.

### Idle auto-shutdown

The app stops the server after `Minecraft:IdleShutdownMinutes` (default 30)
with nobody online, and releases its lease so the CPU/RAM is free for other
things. Set it to `0` to disable. In `appsettings.json`, or as
`Minecraft__IdleShutdownMinutes` in `/etc/conf.d/palkia-web`.

It tracks `joined the game` / `left the game` lines in the log, but never
trusts that alone: once the log says "empty" for the full timeout, it asks
the server directly with `list` and only stops it if the server itself
reports 0 players. If `list` gets no parseable answer, it does **not** shut
down and tries again after another timeout. Each check adds nothing to the
log except that one `list` line at decision time. Shutdown goes through
`rc-service minecraft stop` (a clean SIGTERM save), not the console pipe,
because `supervise-daemon` would just respawn a java that exited on its own.

If it ever fails to shut down when it should, look for lines starting
`Minecraft idle` / `Idle timeout reached` in the app's log - they say whether
the log count, the `list` reply or the stop itself was the problem.

## 9. Keeping the installed copies in sync

```sh
sh deploy/check-installed.sh      # OK / STALE / MISSING for each root-owned file
```

Run it after every pull that touches `deploy/`. `conf.d` files are expected to
differ if you've edited them for this box; that's not a problem. For anything
console-related, `deploy/diagnose-console.sh` goes further (see above).

## 10. AI serving (FreeToken)

Five profiles (`fast`, `small`, `medium`, `big`, `max` - see
`Palkia_FreeToken_Deployment_Rules.md`), each its own OpenRC service
`palkia-ft-<id>` running `ft serve` on a loopback port. Like Minecraft, they
are **never added to a runlevel**: the app takes a broker lease, starts the
profile, and stops it again when idle. Install only the profiles you intend
to use; the rollout order in the rules doc is `medium` -> `small`/`fast` ->
`big` -> `max`.

**Prerequisites:** NVIDIA driver r580+ (CUDA 13), a CUDA 13 toolkit with
`nvcc` (kernels are JIT-compiled on first start; the launcher puts
`/opt/cuda/bin` on `PATH` - change it in `palkia-ft-launch.sh` if Gentoo put
yours elsewhere), `uv`, CPython 3.12.

### a. Service account, FreeToken, models

```sh
useradd -r -m -d /var/lib/freetoken -s /sbin/nologin -G video freetoken
install -d -o freetoken -g freetoken -m 0750 /var/lib/palkia-models /opt/freetoken

su -s /bin/sh freetoken -c 'cd /opt/freetoken && uv venv && uv pip install "freetoken[accel]"'
/opt/freetoken/.venv/bin/ft --version        # record this (rules doc, section 10)
```

Put each checkpoint in the directory named by `FT_MODEL` in its profile conf
(defaults: `/var/lib/palkia-models/<name>`), owned by `freetoken`. Check the
three things the profile confs warn about before trusting a profile: the
checkpoint is quantized small enough for 12GB, FreeToken supports that
architecture + quantization, and the quant kernel actually works on an
RTX 3060 (`ft serve --help` lists the backend flags).

### b. Install the files

```sh
install -o root -g root -m 0755 deploy/palkia-rc-ctl.sh     /usr/local/sbin/palkia-rc-ctl   # allow-list changed - reinstall it
install -o root -g root -m 0755 deploy/palkia-ft-launch.sh  /usr/local/sbin/palkia-ft-launch
install -o root -g root -m 0755 deploy/palkia-ft.initd      /etc/init.d/palkia-ft
install -d -o root -g freetoken -m 0750 /etc/palkia/freetoken /var/log/palkia-ft

for p in medium; do        # add the profiles you want: fast small medium big max
    ln -sf palkia-ft /etc/init.d/palkia-ft-$p
    install -o root -g root -m 0644 deploy/freetoken/profiles/$p.conf /etc/palkia/freetoken/palkia-ft-$p.conf
    install -o root -g root -m 0640 deploy/freetoken/confd/$p.confd   /etc/conf.d/palkia-ft-$p
done
```

Do **not** `rc-update add` any of them.

### c. Upstream keys (generated once, written to both places)

`ft serve` requires its own key (defense in depth behind the loopback bind);
the app's gateway injects it. The launcher refuses to start a profile whose
key is empty. Generate all five at once so the two sides can't disagree:

```sh
umask 077
for p in fast small medium big max; do
    printf 'FT_UPSTREAM_KEY_%s=%s\n' "$(echo $p | tr a-z A-Z)" "$(openssl rand -hex 32)"
done > /etc/palkia/freetoken/keys.env
chown root:freetoken /etc/palkia/freetoken/keys.env && chmod 0640 /etc/palkia/freetoken/keys.env

# the matching lines for /etc/conf.d/palkia-web (paste them in, then restart the app):
sed -E 's/^FT_UPSTREAM_KEY_([A-Z]+)=(.*)$/export FreeToken__UpstreamApiKeys__\L\1="\2"/' /etc/palkia/freetoken/keys.env
```

### d. Smoke test one profile by hand, then hand it back to the broker

```sh
rc-service palkia-ft-medium start
tail -f /var/log/palkia-ft/medium.log /var/log/palkia-ft/medium.err   # first start compiles kernels: minutes
curl -s http://127.0.0.1:1921/health                                   # exempt from the key
rc-service palkia-ft-medium stop
```

`start_pre` refuses to start when the free VRAM / available RAM in the
profile conf isn't there (rules doc, section 7), and says why. While testing,
watch `nvidia-smi`, `free -m`, swap-in/out (`vmstat 1`) and
`cpu.stat` under `/sys/fs/cgroup/*palkia-ft-<id>/` (the exact directory name depends on your OpenRC version). The cgroup limits in the confd file
must agree with the lease in `ModelProfileCatalog` - if a profile is
OOM-killed on load or CPU-throttled, raise **both**, not just the cgroup.
Uncomment the `memory.swap.max 0` block in the `max` confd while evaluating
it, so "only works by paging to swap" fails fast instead of thrashing.

### e. Public access

See `deploy/TAILSCALE.md` (Tailscale Serve, tailnet only).
