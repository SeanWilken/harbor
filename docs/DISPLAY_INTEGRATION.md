# Optional display capability: integration handoff

User-directed architecture agreement, 2026-11-12. This is the current integration direction; it does not announce a working Harbor/display integration.

## Producer alpha now available — 2026-11-12


The initial library extraction and package-only consumer milestone is ready for **integration feedback**.
It is not production Harbor integration and does not unblock the live Desktop view tile by itself.

Portable SDK archive: `display-sdk-0.1.0-local.20261112180315.zip` (local distribution build).
NuGet build: `0.1.0-local.20261112180315`. Browser tarball: `0.1.0`, pinned by the bundle's file-hash manifest.
The bundle contains five packages (Core, Protocol, Identity, Runtime, AspNetCore), browser JS/types,
a prebuilt synthetic hub/outbound-agent consumer, setup notes and SHA-256 file hashes.
It contains no live host configuration, pairing state, credentials or EDID.

Copy/extract the bundle wherever the consuming application keeps its development dependencies.
No DOI checkout, standalone host/tray/PWA or DOI service needs to run. Install only the packages needed.
Use the bundle's `nuget` directory plus the normal NuGet source for dependency restore; install
`browser/receiver-client-0.1.0.tgz` locally with Bun for browser consumers. The bundle README is self-contained.
These are experimental working-tree artifacts, not a GitHub release or public registry publication.

Implemented seams: listener-independent single-publication sessions, media factory and signaling connection,
optional web endpoint mapping with trusted application authorization, configurable receiver API mount,
view-only playback and explicit audio approval. `AuthorizedSession.AudioAllowed` defaults to false by policy;
`IMediaWorkerFactory.StartAsync` receives the trusted session and cancellation. Missing old audio fields mean
video-only, and configured output audio is suppressed for such grants. Identity's private file writer is Unix-specific.

Production Harbor transport/multiplexing, renewable authorization leases, detailed device/session projections,
durable events and supported cross-platform capture remain pending. Harbor should keep its real viewing tile
disabled until those policy/transport/provider checks pass. It can now evaluate package APIs and the synthetic
consumer while roles, redaction, inventory, networking and other administration work proceeds independently.

Requested feedback: package/install friction, lifecycle/cancellation behavior, authorization adapter fit,
required device/session snapshots/events and platform packaging needs. New native-monitor experiments are
independent of this handoff and must not become a dependency of Harbor's inventory or admin functions.

## Product ownership and installation

Harbor is the independently installable machine/repository ledger, inventory, troubleshooting and administration/control plane: a personal tailnet administration console. It owns its machine/access catalog, configuration workflows, policy and audit UX. This broader direction does not mean configuration mutation, remote input or arbitrary administration is already implemented.

DisplayOverInternet is an independently installable lightweight display and workstation-view replication product. Its display implementation will also be consumable as libraries and platform assets by Harbor and other applications.

**Harbor must not require the standalone DisplayOverInternet app, daemon, tray, PWA or repository checkout.** DisplayOverInternet must not require Harbor. Display viewing is an optional Harbor capability; its absence or failure must not prevent inventory, repository operations or other administration features from working.

| Installation | Expected behavior |
| --- | --- |
| Harbor without display support | Full available Harbor inventory/admin functionality; desktop viewing shown as not installed or unsupported |
| Harbor with display support | Bundles the display runtime and browser SDK; supplies explicit authorization and persistence adapters |
| Standalone DisplayOverInternet | Uses the same runtime with its lightweight host, pairing and receiver |
| Optional external display provider | Connects through a separately designed versioned service client; not the prerequisite integration path |

This refines earlier DisplayOverInternet notes assigning the general access launchpad exclusively to that product. Harbor owns its general fleet/admin access experience; reusable display packages own display behavior. A viewer remains view-only unless a separate control capability is explicitly implemented and authorized.

## Available now versus pending

| Building block | Current status |
| --- | --- |
| `Display.Core` | Existing .NET display validation/lifecycle library |
| `Display.Protocol` | Existing version 1 host/channel/receiver-session/pairing DTOs and signaling envelope validation; not a complete admin/device/event API |
| `Display.Identity` | Existing standalone hashed bearer-grant persistence and expiry; private-file writer is Unix-specific, storage/policy adapters still needed |
| `@wireless-display/receiver-client` | Existing framework-independent TypeScript browser discovery, pairing and view-only playback; used by the standalone React receiver |
| `Display.Runtime` | Initial transport-independent single-publication sessions, worker supervision, capacity, authorization checks and cleanup are packaged |
| ASP.NET composition API | Initial `Display.AspNetCore` subscription mapping is packaged; discovery/enrollment/static UI and policy remain application-owned |
| Device projections/session events | Planned scoped contracts; no durable per-device last-seen/history API today |
| Package distribution | Portable local alpha bundle with five NuGet packages, browser tarball and prebuilt synthetic consumer; no public feed or production Harbor integration |

Do not add a dependency on the standalone host executable or copy its `Program.fs`, pairing scripts, `.local` state, worker internals or React application into Harbor. Do not treat the proposed package names or pseudo-contracts as stable APIs before the first runtime milestone lands.

## Work Harbor can continue now

- Repository inventory, fetch jobs, cross-machine comparisons, release tracking and fleet resource/service views can proceed independently.
- Consumer authorization, redaction, audit and resumable ledger/feed work can proceed without display packages. The consumer bearer scopes described in `design.md` are planned, not the current tailnet guard implementation.
- General access-catalog UX can be designed around optional capabilities, explicit permissions and endpoint references. Use fixture data for display states; do not imply a live adapter or current-browser authorization from those fixtures.
- Define presentation for not-installed, unsupported, unavailable, ready and stale observations. Keep actual publication availability, current-user authorization and active playback as separate facts. Exact shared DTOs will be agreed with the runtime rather than invented as a parallel display protocol.
- Continue bounded, explicitly authorized machine/configuration workflows using Harbor's own design process. Existing remote jobs remain fetch-only; a display integration does not authorize arbitrary shell execution or add new job kinds.

The Desktop view and Remote control tiles in `ui/src/components/MachinePanel.tsx` are currently placeholders. Their tooltips still mention the older catalog/status integration assumption. Keep them disabled until an actual provider is wired and validated, or update their wording in a separate UI change to explain that the optional capability is unavailable.

## Integration seam to preserve

Harbor may expose an optional capability-provider boundary in its own architecture, but need not implement a plugin framework or display subsystem ahead of the packages. A display provider should contribute only the information and operations appropriate to the caller: display host identity, publication descriptors/readiness, authorized view entry points, and later scoped device/session projections.

Keep display existence, publication and subscription separate. A view request must not implicitly create an output. Disconnection must not remove a display. Reconnect must not silently extend a grant. A second viewer or desktop mirror is not evidence of an extended native monitor.

Use explicit stable-ID associations: Harbor identifies remote agents by Tailscale node StableID plus configured machine name, while the current standalone display host has its own persisted logical ID. Multiple agents can exist on one node. Do not join solely on hostnames, IPs or labels, or equate a receiver credential context with a physical machine. Persisted standalone host IDs are not cryptographic fingerprints.

Provider observations need their own source timestamp and readiness state. Keep Harbor push/ingest time separate; forwarding an old observation in a fresh push must not make it look current. Capability errors should be isolated from inventory scans and push availability.

## Deployment, authorization and media

Embedded means the installing application's host uses the runtime library; native capture/encoding may still use a supervised child worker. GStreamer/codecs, graphical-session access and platform permissions remain dependencies that the optional workstation feature must detect/package/document. A hub-only install needs no capture dependencies. Windows capture/virtual-display support remains separately validated work.

Capture runs on the target workstation beside the appropriate graphical session, not automatically inside the central Harbor hub. A system-service agent may need an explicit session bridge. Remote signaling routing, HTTPS and media reachability still need a deployment design; embedding is not a fleet transport solution. Preserve direct WebRTC media delivery where supported rather than carrying video through inventory snapshots, Postgres or the job queue.

Harbor supplies an explicit display authorization adapter. Its current known-tailnet-peer read access and machine enrollment are not sufficient permission to view a desktop. Missing policy fails closed. Distinguish viewing, device enrollment/revocation, native display provisioning and future input/control. Reuse proven authentication mechanisms; do not forward caller-controlled identity headers or copy browser cookies between independent applications.

The standalone product retains its existing pairing by default. Harbor may use its own grant store through an adapter when implemented. That does not migrate existing paired browsers automatically. Safari and a Home Screen application can be distinct credential contexts. The current credential is an HttpOnly bearer cookie, not an SSH-style key.

HTTP cookies are hostname/path scoped, not port scoped. Embedded endpoints need configurable route prefixes, cookie names/paths, Origin policy and storage locations. Browser playback needs an appropriate secure context; Harbor's currently documented HTTP UI is not a completed embedded-viewer deployment. Do not weaken authorization or assume cross-origin cookie federation to make a prototype work.

Detailed device inventory and sessions require scoped visibility; do not add them to broadly readable fleet snapshots by default. Existing `/api/session` describes only the authenticated receiver's own grant, and `/api/status` counts allocated subscriptions including negotiation. Neither proves a person is watching. Runtime lifecycle events should flow into an injected sink so Harbor can retain ledger entries without imposing Postgres on standalone display. Never record credentials, pairing codes, screen contents or raw SDP/ICE in the fleet ledger.

## Delivery responsibilities and acceptance

DisplayOverInternet's next milestone:

1. Extract the proven worker/subscription lifecycle behind a configurable runtime/media adapter, preserving existing standalone pairing and playback.
2. Add composable ASP.NET endpoints and configurable receiver API addressing, with explicit state/asset paths and authorization boundaries.
3. Prove a minimal alternate host using the packages and a synthetic source, without running the standalone app or checking out Harbor. Any fixture authorization remains restricted to the test composition/source.
4. Verify standalone and alternate-host negotiation, playback, capacity and disconnect/revocation cleanup; verify missing/disabled media does not break the parent application.
5. Provide installable artifacts, dependency/setup instructions, version compatibility and an example consumer before requesting Harbor integration.

Harbor's integration milestone, after those artifacts are ready:

1. Opt in to the supported runtime packages on a target workstation; connect explicit authorization, persistence and event adapters as available.
2. Wire optional capability observations and the browser SDK into the existing machine/access UX without requiring a second application installation.
3. Validate disabled/unavailable capability, authorization denial, stale observations, successful decoded video, cleanup and continued inventory operation after media failure.

Choose one active owner for each managed display resource. Running the standalone product and Harbor on the same workstation must not result in both automatically creating/managing the same output. General remote control/RDP remains a separate capability and authentication path.

## Coordination references

The producer is the DisplayOverInternet repository (currently private). These are development references, not runtime requirements.

Relevant producer documents: `docs/COMPOSABLE_RUNTIME.md` (current composition plan), `docs/SDK.md` (implemented library surfaces), and `docs/HARBOR_INTEGRATION.md` (source assessment). Runtime extraction is pending; coordinate against those documents and the actual released artifacts rather than assuming the current feature branch is a stable integration SDK.

This handoff changes documentation only. No Harbor provider, package dependency, endpoint, permission, database migration or service configuration is enabled by it.
