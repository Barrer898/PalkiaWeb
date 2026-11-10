# Palkia AI setup

How the local-model side of Palkia fits together and how to bring it up, in
order. Exact install commands live in `deploy/README.md` section 10; this
document is the map, the app-side configuration, and the troubleshooting
notes. The design rules behind all of it are in
`Palkia_FreeToken_Deployment_Rules.md`.

Nothing here has been run against real hardware yet. Every context size,
memory ratio and lease is a starting value to be measured, not a promise.

## 1. What you get

- A **Chat** page on the website (any signed-in user).
- An **OpenAI- and Anthropic-compatible API** at `/api/llm/*` for coding agents
  and scripts, behind per-key authentication.
- **Five model profiles**, loaded on demand, never all at once:

| Profile | Model (planned) | Context target | Concurrent requests | Lease (cores / RAM / VRAM) | Loopback port |
|---|---|---:|---:|---|---:|
| `max` (experimental) | Qwen3.6-35B-A3B, MoE offload | 110,576 | 1 | 2 / 10 GB / ~10 GB | 1923 |
| `big` | gpt-oss-20B, MoE offload | 65,536 | 1 | 2 / 9 GB / 8 GB | 1922 |
| `medium` | Qwen3.5-9B, GPU-resident | 65,536 | 1 | 1 / 3 GB / 10 GB | 1921 |
| `small` | Qwen3.5-2B | 65,536 | 2 | 1 / 2 GB / 4 GB | 1920 |
| `fast` | Qwen3.5-0.8B | 32,768 | 4 | 1 / 1.5 GB / 3 GB | 1919 |

The authoritative numbers are in `Palkia.Core.Ai.ModelProfileCatalog`; the
per-profile files in `deploy/freetoken/` must agree with it by hand.

Only `small` + `fast` are meant to run together. Everything else takes an
exclusive slot, and the resource broker also makes models wait for Minecraft
(and the other way round) when the shared CPU/RAM/VRAM budget is full.

## 2. How a request flows

```text
Chat page / agent
      |  (Bearer key)
      v
Palkia gateway  /api/llm/v1/...          authenticate -> allowed profile? ->
      |                                  per-key concurrency permit -> broker lease
      v                                  (queue if the budget is full) -> start model
palkia-ft-<profile>  (OpenRC, user 'freetoken')
      |  ft serve on 127.0.0.1:<port>, its own --api-key
      v
RTX 3060 + host RAM
```

- Nothing but Palkia talks to `ft serve`: it binds loopback and requires its
  own key. Agents never see that key.
- A model that isn't running is started by the first request that needs it.
  The request waits (up to 15 minutes) while the broker finds room and the
  weights load.
- A profile with no traffic for `IdleShutdownMinutes` (default 15) is stopped
  and its lease released.

## 3. Bring-up checklist

Do these in order; each one is testable before the next.

1. **Prerequisites** - NVIDIA driver r580+, CUDA 13 toolkit (`nvcc`), `uv`,
   CPython 3.12. First start JIT-compiles kernels, so it is slow once.
2. **FreeToken + service account + models** - `deploy/README.md` section 10a.
   Record `ft --version`. Download or convert each checkpoint into the
   directory named by `FT_MODEL` in its profile file. Before trusting a
   profile, confirm three things: the checkpoint is quantized small enough for
   12 GB, FreeToken supports that architecture and quantization, and the quant
   kernel works on an RTX 3060 (sm_86). NVFP4 in particular is unverified here
   (see Troubleshooting).
3. **Install the OpenRC pieces** - section 10b: launcher, `palkia-ft` init
   script plus one symlink per profile you want, profile `.conf` and `.confd`
   files, and the reinstalled `palkia-rc-ctl` wrapper (its allow-list changed).
   Install only the profiles you intend to use. Do **not** add them to a
   runlevel.
4. **Upstream keys** - section 10c. One command writes `keys.env` and prints
   the matching `FreeToken__UpstreamApiKeys__*` lines for
   `/etc/conf.d/palkia-web`.
5. **App configuration** - section 4 below.
6. **Smoke-test one profile by hand** - section 10d. Start it with
   `rc-service`, watch the logs and `nvidia-smi`, hit `/health`, stop it.
7. **Roll out in this order**, only moving on when the current step is stable:
   `medium` -> `small` and `fast` (alone, then together) -> `big` -> `max`.
8. **Public access** (optional) - `deploy/TAILSCALE.md`.

## 4. App configuration

All of this goes in `/etc/conf.d/palkia-web` as `export` lines (ASP.NET
reads `A__B__C` as `A:B:C`), then restart the app.

**Where the website chat sends its requests.** The chat page calls the
gateway on the app's own listener. The default is `http://localhost:5000`;
the deployed service listens on `127.0.0.1:5080`, so set:

```sh
export Chat__BaseUrl="http://127.0.0.1:5080"
```

**Upstream keys** - from step 4 (`FreeToken__UpstreamApiKeys__<profile>`).

**Client keys.** There is no admin page for keys yet, so they are configured
here. Generate each with `openssl rand -hex 32`:

```sh
# the website chat's own credential - the value must appear BOTH here ...
export FreeToken__WebsiteApiKey="<random>"
# ... and as a normal key entry, or the gateway will reject the chat page:
export FreeToken__ApiKeys__0__Label="website"
export FreeToken__ApiKeys__0__Key="<same random value>"

# one entry per agent / person; AllowedProfiles empty = all profiles
export FreeToken__ApiKeys__1__Label="coding-agent"
export FreeToken__ApiKeys__1__Key="<another random value>"
export FreeToken__ApiKeys__1__AllowedProfiles__0="medium"
export FreeToken__ApiKeys__1__AllowedProfiles__1="small"
```

Keep `/etc/conf.d/palkia-web` readable by root only.

**Optional behaviour:**

```sh
export FreeToken__IdleShutdownMinutes="15"      # 0 = never auto-stop
export FreeToken__LoadTimeoutSeconds="900"      # how long a start may take before it counts as failed
export FreeToken__AlwaysOnProfiles__0="fast"    # kept resident; only tiny profiles belong here
```

## 5. Using it

**Website:** open **Chat**, pick a model in the table, type, Ctrl+Enter to
send. "Warm up" starts a stopped model ahead of time so the first message
doesn't wait for it. Admins also get a Stop button.

**Agents and scripts:** base URL `https://<machine>.<tailnet>.ts.net/api/llm`
(see `deploy/TAILSCALE.md`), or `http://127.0.0.1:5080/api/llm` on the box
itself.

```sh
curl -s -H "Authorization: Bearer $KEY" $BASE/v1/models

curl -s -H "Authorization: Bearer $KEY" -H "Content-Type: application/json" \
  -d '{"model":"qwen3.5-9b","messages":[{"role":"user","content":"hello"}]}' \
  $BASE/v1/chat/completions
```

- OpenAI-compatible clients: base URL `$BASE/v1`.
- Anthropic-style clients (`/v1/messages`): base URL `$BASE`.
- Use the model ids from `/v1/models`.
- Limits you will hit by design: **429 + Retry-After** when a key is over its
  per-profile concurrency; a cold model makes the request wait (minutes for
  the big ones) - agents should use a generous timeout and retry on timeout.

## 6. Profile states

| State | Meaning |
|---|---|
| Stopped | Not running, no lease held. |
| Starting | Waiting for a broker lease (shows a queue position) or for the service to come up. |
| Loading | Service is up, weights are still loading from NVMe. |
| Ready | `/health` answers ok; requests are served. |
| Failed | The start did not complete (bad checkpoint, OOM, timeout). The lease is released. |

## 7. Validation and acceptance

For each profile, at each context size you intend to offer, record: peak
VRAM, peak host RAM, swap in/out, CPU load, prefill speed, decode tokens/sec,
and errors (`nvidia-smi`, `free -m`, `vmstat 1`, `/v1/stats`). A profile is
usable only if it:

- loads without memory errors or OS instability,
- completes a realistic long-context task,
- does not continuously thrash swap,
- keeps acceptable latency for its role,
- behaves correctly with tool calls in the real agent client,
- respects the concurrency limits.

If `max` only works by paging to swap, classify it as unusable at that
configuration rather than successful. Uncomment the `memory.swap.max 0` block
in its confd file while evaluating so that case fails fast.

Only raise context or concurrency after the current setting has shown stable
headroom. Compare `offload` against `hybrid` (profile `FT_MOE_STRATEGY`) with
identical model, context, prompt and concurrency before choosing one.

## 8. Troubleshooting

| Symptom | Likely cause | What to do |
|---|---|---|
| Chat says "no valid gateway key" (401) | `FreeToken__WebsiteApiKey` unset, or its value isn't also an `ApiKeys` entry | Section 4, client keys |
| Chat says it couldn't reach the model server (502) | `Chat__BaseUrl` points at the wrong port | Set it to the app's real listener |
| Profile start refused: "only N MiB VRAM free" / "RAM available" | Another profile, Minecraft or a desktop session holds the memory | `nvidia-smi`, `free -m`; stop the other service |
| Profile goes Failed during load; `.err` shows `Killed` / dmesg shows OOM | The cgroup `memory.max` (or real RAM) is too small for that model | Raise the confd limit **and** the lease in `ModelProfileCatalog` together |
| Load is very slow and the disk/swap is busy | Weights paging through swap | Smaller quantization, lower context, or more RAM; do not just add swap |
| Everything is slow with one CPU core pegged | `cpu.max` throttling (profile capped at 1-2 cores) | Check `cpu.stat`; raise the confd cap and the lease together |
| NVFP4 profile fails to start or errors in kernels | Backend not usable on sm_86 | Force one with `FT_EXTRA_ARGS="--nvfp4-backend marlin"` (valid: auto, marlin, flashinfer, triton; see `ft serve --help`) |
| First start takes many minutes | One-time kernel JIT compile | Wait; later starts are faster. Needs `nvcc` on the launcher's PATH |
| `small` fails to start when `fast` is already running | `--memory-ratio` is a fraction of *free* VRAM, so start order matters | Start `small` first (see `small.conf`) |
| `rc-service palkia-ft-x start` says not in allow-list (via the app) | Old wrapper still installed | Reinstall `palkia-rc-ctl`; `deploy/check-installed.sh` shows drift |

Logs: `/var/log/palkia-ft/<profile>.log` and `.err`. Config drift check:
`deploy/check-installed.sh`.

## 9. Security notes

- Never expose the `ft serve` ports. They are loopback-only and key-protected;
  keep it that way.
- Agents authenticate with their own gateway key, never an upstream key.
  Separate key per agent or person where practical.
- Prefer Tailscale Serve (tailnet only). Do not use Funnel (public internet)
  until the gateway throttles repeated failed keys and per-IP abuse.
- Don't log prompts. Operational metrics (latency, tokens, queue) are fine.
- `/etc/palkia/freetoken/keys.env` is `root:freetoken 0640`;
  `/etc/conf.d/palkia-web` should be root-only.

## 10. Known gaps

- No admin page for gateway keys yet (the store exists; keys are configured
  through environment variables for now).
- The website chat uses **one shared key**, so the per-key concurrency limit
  is shared by every website user: while one person's request runs on a
  one-at-a-time model, everyone else gets "busy". Per-user limits for the
  chat page need a per-user caller identity.
- The chat page shows the finished reply, not tokens as they stream.
- FreeToken's documented model list covers the 35B MoE, gpt-oss and Gemma, not
  the Qwen3.5 9B/2B/0.8B checkpoints. The same architecture family should
  work, but that is unverified until rollout steps 2-3.
