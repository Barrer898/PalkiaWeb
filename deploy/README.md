# Deploying Palkia on Gentoo (OpenRC)

These steps assume you're building/publishing on the same box (or copying a
published output over). None of this has been run against a real OpenRC host
yet - the scaffold was hand-written without the .NET SDK available, so treat
this as a first draft and expect to fix small things.

## 1. Install the .NET SDK

```sh
emerge --ask dev-dotnet/dotnet-sdk-bin:8
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
separate migration step is needed yet (see the comment in `Program.cs` about
switching to real EF Core Migrations before phase 2 changes the schema).

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
