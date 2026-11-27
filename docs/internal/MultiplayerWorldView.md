# Private world-view delivery

`IPlayer.View` and `IEnumerable<IPlayer>.View()` extend the scoped world-view
controls in SDK 0.2.0. The latter captures at most 256 input entries now, removes
duplicate handles and returns one lease over the acquired child leases. A failed
call rolls back those child leases. New members carry the 0.2.0 version floor.
Existing interface implementers must add `IPlayer.View` when rebuilding.

The runtime owns the ledger, authority checks, scope tracking and delivery. Worlds
owns native drawing and maps authored scene/entity ids to local entities. Startup
and follow activate the renderer before controller callbacks, so a joined player
needs no server controller or author networking code to receive its view.

## Derivation and authority

- The shortest applicable fog wins, with Dark, Haze, Smoke breaking distance ties.
- Hides are the union of applicable leases; there is no reveal override.
- The newest targeted environment wins over every shared environment. Without a
  targeted environment, the newest shared one wins; without either, the map's
  original environment applies while native ownership remains intact.
- Participant code is refused even offline. Host and solo authoritative code
  apply immediately. Joined scripted code gets a lifetime-owned shadow; joined
  player intent is refused. Client-local and standalone mods cannot publish
  networked views. Server-only authoritative code may use the runtime service.
- Audiences and their role decisions stay on the host. No audience list, lease
  owner, role, other recipient's state, address or transport identity is encoded.

## Delivery and limits

The consumer uses the private-delivery runtime's system kind 8 (`PerPlayer`), exclusively through reliable
`Only(player)` sends. The inherited channel rejects broadcasts, datagrams and
client payload sends; clients return transport credits only. Its authenticated
epoch/generation checks, independent peer windows and stall handling remain
unchanged. The window is 1 MiB and 1,024 messages per recipient.

The world-view payload revision is 1. Each full snapshot is Begin, zero or more
Targets messages, then End. Begin contains the opaque world-session id, effective
preset/fog and expected target count. Each target contains a kind plus authored
scene/entity ids, a replicated spawn reference or a player reference. End commits
the snapshot atomically. A new Begin replaces an incomplete predecessor; ordered
delivery makes this safe when a world changes halfway through a baseline.

- At most 1,024 distinct hidden targets in each effective view, including unresolved
  targets; at most 4,096 live leases in a world, shared across callers and audiences.
- Presets: 1–64 printable characters; fog: finite 1–500 metres and a defined tint.
- World/reference strings: at most 128 printable characters, strict bounded UTF-8.
- At most 16 targets per chunk and 20 KiB per message. A full 1,024-target baseline
  is 66 messages: Begin, 64 target chunks and End.
- At most four sends per recipient per runtime update, always gated by `CanSend`.
  Each sender keeps its last and pending snapshots and one cached chunk. Changes
  coalesce while a snapshot is in flight; there is no unbounded change journal.
- A receiver keeps one committed and one assembling snapshot. Wrong senders,
  revisions, kinds, bounds, truncation, duplicates and trailing bytes invalidate
  the view stream until its generation changes. Payloads are never logged.

## Lifetime and native ownership

Every stream start, rejoin and replacement receives a fresh effective baseline.
Scope disposal changes the next snapshot. Reconnect grace retains host leases;
expiry or identity replacement removes the departed player's targeted leases.
Network-session replacement/end clears leases and caches. Opening a solo world
to guests preserves its shared view. World identity prevents an old snapshot
from applying to a replacement world, including when data arrives before loading.

Stable references wait within the target bound and are reconciled at most four
times per second. Replacement avatar objects are resolved again. At rest, no
network payload or native setter is produced; the bounded reference reconciliation
still checks for arrivals/replacements. Fog keeps the existing 0.5-second fade and
immediate reduced-motion behavior. Only its owned HDRP resources are removed.

Hiding retains renderer values and restores only values still as written; nested
authored renderers, colliders, lights and audio retain their existing behavior.
Environment restoration checks the installed native environment-instance set.
If the game or another provider replaced it, the view yields rather than applying
an old preset over the replacement. Actual preset switching and HDRP appearance
remain native acceptance checks at both supported window sizes.

This is confidential presentation delivery, not anti-cheat: a modified client can
ignore its own fog or hiding. Keep secret state in private state contracts, and
keep role/audience decisions in authoritative code.

## Focused regression entrypoints

- ModManager harness `--world-view`: existing solo rules, native ownership seams,
  provider binding, fakes and the three-runtime consumer tests.
- ModRuntime harness `--world-view`: the same consumer against the production
  runtime assembly, using the in-memory credit/generation transport.
- ModManager harness `--sdk-api-baselines`: exact SDK metadata and version floors.

Native checks cover distinct player views, preset and fog restoration, late joins,
world replacement, delayed targets and reduced motion at 2560×1440 and 1920×1080.
