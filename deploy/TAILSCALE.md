# Exposing the AI gateway with Tailscale Serve

The gateway lives inside the Palkia app at `/api/llm/*` (OpenAI-style
`/v1/chat/completions`, `/v1/models`, Anthropic-style `/v1/messages`, ...).
Kestrel stays bound to `127.0.0.1:5080`; nothing about this opens a port on
your router. Tailscale Serve publishes that loopback port **to your tailnet
only**, with a real HTTPS certificate for `<machine>.<tailnet>.ts.net`.

This replaces the "nginx + TLS" layout from the deployment rules doc. The
rules that still apply: authenticated access, per-key rate/concurrency
limits, request-size limits, a queue instead of overload, and the raw
`ft serve` ports (1919-1923) staying on loopback behind their own
`--api-key`.

## One-time setup (Gentoo/OpenRC)

```sh
emerge --ask net-vpn/tailscale
rc-update add tailscaled default && rc-service tailscaled start
tailscale up
```

In the Tailscale admin console, enable **MagicDNS** and **HTTPS
Certificates** for the tailnet (Serve needs both).

## Publish

Option A - **API only** (recommended for agents): the Blazor UI and its
login cookies are not reachable over the tailnet.

```sh
tailscale serve --bg --https=443 --set-path=/api/llm http://127.0.0.1:5080/api/llm
```

Option B - the whole app (website + API):

```sh
tailscale serve --bg --https=443 http://127.0.0.1:5080
```

Check what is published, and that the path mapping kept `/api/llm` intact
(the target URL must include the path, or Serve strips it - I have not been
able to test this on your box, so verify):

```sh
tailscale serve status
curl -s -H "Authorization: Bearer $KEY" https://<machine>.<tailnet>.ts.net/api/llm/v1/models
```

`tailscale serve reset` removes everything it published.

## Client configuration

Models are listed by `/v1/models`; use those ids (`qwen3.5-9b`, ...).

- OpenAI-compatible clients: base URL
  `https://<machine>.<tailnet>.ts.net/api/llm/v1`, API key = a Palkia gateway
  key (created in the app), never an `ft` upstream key.
- Anthropic-style clients (e.g. coding agents that speak `/v1/messages`): base
  URL `https://<machine>.<tailnet>.ts.net/api/llm`.

A cold start (weights coming off NVMe, first-use kernel JIT) can take
minutes. The gateway holds the request while it loads; whether an
intermediate proxy or the client times out first is untested - agents should
retry on timeouts rather than assume failure.

## Tailnet-only vs. the public internet

Serve is reachable only by devices/users on your tailnet (add agents'
machines as tailnet nodes, or share the node). That is the access-control
layer; the gateway keys are a second one.

`tailscale funnel` would put this on the **public internet**. Don't, yet:
the gateway has per-key concurrency limits but no throttling of repeated
failed-key attempts or per-IP limits. Keys are 256-bit random so guessing is
infeasible, but an open endpoint still needs abuse protection (failure
throttling, per-IP limits, an upstream request cap) before it should be
exposed that way.
