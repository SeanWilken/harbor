# Desktop viewers in harbor: the contract

Harbor's side of the integration with a desktop viewer (today: DisplayOverInternet). The viewer's
side is its own `docs/HARBOR_INTEGRATION.md`. Between them, these two documents are the interface:
each product's agent works only in its own repository, and a change that crosses the line starts as
an edit to the providing side's document, which the other side then picks up. Neither side reviews
the other's code.

Contract version: **1** (2026-11-13).

## Who owns what

| Harbor | The viewer |
| --- | --- |
| Machine inventory, the hub, the iPad entry point (`https://<hub>:7443`) | Capture, encoding, WebRTC media |
| The `access` catalog: which machine has which viewer or RDP host | Device pairing: codes, grants, expiry, the paired-device list, revocation |
| The `/display/<machine>/` pass-through | Its receiver page, API and signaling, under its own root and an optional mount |
| Harbor's own roles (`hub.admins`, `hub.viewers`) gating the hub | Its own authorization of every viewer request; harbor's roles never replace it |

Neither product requires the other. Harbor without a viewer shows Desktop view as "not configured";
a viewer without harbor works on its own address. Remote control (RDP) is a separate entry and a
separate sign-in; it is not part of this contract.

## What harbor relies on (v1)

1. **A plain https origin per machine**, on the machine's own tailnet name (e.g.
   `https://<machine>.<tailnet>.ts.net`), declared in that machine's harbor config:
   `"access": [{ "kind": "display", "url": "https://<machine>.<tailnet>.ts.net" }]`.
2. **A mount at `/display/<machine>`**, where `<machine>` is harbor's machine name (lowercase
   `[a-z0-9-]`), configured on the viewer, set explicitly on both sides and never derived from
   hostnames. Under it:
   - `<mount>` redirects to `<mount>/`; the receiver page is `<mount>/`;
   - the API is `<mount>/api/…`, and `GET <mount>/api/session` answers **200 or 401 with a JSON
     body**. Harbor uses that answer to decide that the mount works (Desktop view then says "through
     harbor");
   - signaling is a WebSocket under `<mount>/api/subscribe/…`.
3. **Harbor's origin is accepted only under the mount** (`https://<hub>.<tailnet>.ts.net:7443`),
   never at the viewer's root.
4. **The pairing cookie is scoped to the mount path** (`Path=<mount>`, `Secure`, `HttpOnly`,
   `SameSite=Strict`, no `Domain`), so machines behind the one hub origin stay apart.
5. **Responses that carry credentials are `no-store`.**

If any of these changes, bump the version in the viewer's document and say what harbor must do.

## What harbor guarantees

- **The path is passed through unchanged** (no prefix stripping), with the method, body, `Origin`,
  `Cookie` and `Set-Cookie`. WebSocket upgrades pass through; the idle timeout is 30 minutes.
- **Harbor forwards only to the `display` URL in the latest snapshot of an approved machine**, and
  only when its host is that machine's own tailnet name (the hub's own name for the hub machine). It
  never forwards to loopback, LAN addresses or another machine, whatever a config claims.
- **Harbor removes its callers' `Tailscale-*` identity headers** before forwarding. It adds no
  forwarding headers.
- **Harbor never reads, logs or stores** the viewer's cookie, pairing codes or the body of a pairing
  request. Forwarding logs are kept to warnings and errors. Harbor's UI has no service worker.
- **Harbor serves only its own bundled code** on its origin. That origin is as trusted as the viewer
  (same-origin with every mounted viewer), and harbor treats it that way.
- **Media never passes through harbor.** Only the page, the API and signaling do.
- **The Desktop view tile opens the mount in the same window**, so on an iPad the pairing lives in
  harbor's Home Screen app. Without a working mount it links to the viewer's own origin in a new tab.
- **A pairing made at the viewer's own origin and one made through harbor are separate devices.**
  Harbor never copies or migrates credentials between them.

## Open items for the viewer

Nothing here blocks harbor. These are suggestions for the viewer's own backlog; it decides.

1. **The `display` entry is a top-level `access` key.** A note in the viewer's
   `HARBOR_INTEGRATION.md` says harbor rejects `{ "kind": "display" }` and fails to load its whole
   config. That is only true inside `services` (health checks); as a top-level `access` entry it
   loads, and builds older than the `access` feature ignore it. Please correct the note.
2. **Pairing QR targets the mount when one is configured**, so a scan opens it in harbor's app
   context.
3. **The paired-device list and revocation in the viewer's tray** are entirely the viewer's own work.
   Harbor does not need to see devices; if it ever shows a count, it would be a separately agreed,
   read-only field.
4. **Conformance tests on the viewer's side** for items 2–5 above (mount routes, origin only under the
   mount, cookie path, `no-store`). Harbor tests its side (forwarding rule, machine names).
5. **Media reachability** (firewall rules for WebRTC on Windows) stays the viewer's install concern;
   a working page through harbor doesn't prove media reachability.

## Later, not now

- **Signaling over harbor's agent link** (no browser-facing listener on the workstation) is still the
  end-state idea. The pass-through is the interim and does not replace it. It would be a v2 of this
  contract, designed in the viewer's document first.
- **Embedding the viewer's packages in harbor** (the SDK alpha) stays optional. Harbor won't depend on
  them until the viewer marks a stable release.
- **Harbor showing viewer state** (installed, media available, active sessions) would need a scoped,
  read-only field agreed here first; it never goes into broadly readable fleet snapshots by default.

## Shared ground rules

- Tailnet only; nothing is exposed to the internet.
- No secrets or personal connection details in public repositories. Harbor's repository is public:
  examples use `<hub>`, `<machine>`, `<tailnet>`, `example.ts.net`.
- Viewing is view-only. Keyboard, pointer or touch input needs its own explicit, visible, expiring
  and revocable grant, designed separately.
- Every pairing code is one-use and short-lived, and is shown only to whoever stands at the machine.
