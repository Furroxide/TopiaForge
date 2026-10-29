# Private per-player delivery

`PerPlayerState<T>` (SDK 0.2.0) uses the existing contract generator and
`[ReplicatedState("id")]` declaration. The runtime binds it automatically, just as
it binds shared state. A field has a non-secret default and one value per admitted
player. The host calls `Set(player.Id, value)`; participant code calls
`Get(session.Players.Local.Id)` and observes `SubscribeChanged` for its own changes.
Reads, writes and notifications use codec-detached copies. No author serialization,
audience filtering, reconnect messages or frame polling is needed.

```csharp
[ReplicatedState("card")]
private PerPlayerState<Card> card = new(new Card());

public sealed class Card
{
    [NetworkBound(96)]
    public string Text { get; set; } = "";
}

// In authoritative code, after the player is admitted:
card.Set(player.Id, new Card { Text = "Your private assignment" });
```

The default is part of the mod assembly and must never contain a secret. A client
receives only its own assigned value; it cannot read another player's value or
write any value. A host can read and assign admitted players, including players
retained during reconnect grace. Callers must check the returned operation result.
Private state is not an anti-cheat boundary against the host or trusted in-process
mods. Do not put private data in a shared DTO, command response to another player,
or broadcast presentation event.

## Contract and lifetime

- `IPerPlayerStateSession` is an additive provider SPI; `IMultiplayerSession` is
  unchanged. A provider without it fails binding as `Unavailable`, without a
  shared-state fallback. The production provider and test engine both implement it.
- Generated codecs retain their normal DTO restrictions. Private codecs are
  limited to 16 KiB. The generator rejects larger bounds and non-coalesced delivery.
  `MaximumUpdatesPerSecond` is supported; a new baseline bypasses that rate limit.
- Changing a field between shared and private changes its contract digest. Existing
  shared contracts keep their digests and namespaced IDs. The descriptor's state
  inventory still lists both kinds; confidentiality is part of the schema hash.
- The engine admits at most 64 private fields across mods. It retains one row per
  admitted participant, bounded by the existing participant/grace inventory. A
  client retains at most 64 received fields (at most 1 MiB of value bytes), allowing
  a contract registered after receipt to adopt its own value.
- Host writes join the existing canonical transaction: failure rolls them back.
  Client prediction cannot write private state. Local notifications occur at the
  existing settle point, after commit, and never report another player's changes.
- Reconnect within grace keeps host values but starts a new baseline. Expiring a
  participant removes its private rows. Session replacement/end clears values and
  received caches. Facade disposal unregisters its fields; a removal message resets
  the remote value to its declared default. Rebinding starts from the frozen default.
- Until its first private baseline arrives, a client reads its declared default.
  Subscribe to changes to refresh local presentation. Shared-session Ready does not
  promise that private presentation has already arrived.

## Engine wire channel

System kind **7 (`PrivateState`)** belongs to the engine, separate from shared
snapshots, shared state deltas and their intern tables. It cannot be opened by a
runtime consumer. The existing authenticated link, session epoch, join generation,
ordered delivery and per-stream credits apply. Each connection's send history is
discarded when its generation changes. A baseline starts with Reset, followed by
complete field values; a delta replaces one changed field. Changes coalesce while
credits or the downstream rate hold a sender back. Each message is under 20 KiB;
there is no unbounded chunk assembler or change journal.

The payload uses the engine's existing bounded primitives:

| Field | Encoding |
| --- | --- |
| Envelope revision | byte, currently 1 |
| Operation | byte: 0 Reset, 1 Put, 2 Remove |
| Recipient | bounded participant identity; must equal the receiving local player |
| Mod owner and field ID | bounded identities, present on Put and Remove |
| Field version and value | varuint and bounded bytes, present on Put |

Only the authenticated host can send these payloads. A joined player sends credits
only. Wrong recipient, revision, operation, bounds, missing baseline, repeated Reset,
invalid DTO and trailing data fail the connection. Stale epochs/generations are
discarded before decoding. No private payload is included in diagnostics.

## Runtime consumer channel

System kind **8 (`PerPlayer`)** is available through the existing
`IInternalSystemChannel` seam for private runtime features. It accepts only
host-to-`Only(player)` reliable messages. Broadcast targets, client sends and
datagram sends are refused. It has the standard 32 KiB payload cap, 1 MiB/1,024-message
host window, independent peer credits, stall handling and generation reporting.

A consumer owns its payload schema, derives each player's data on the host, applies
the host's local player's value directly in solo/listen-host play, and sends remote
baselines in chunks of at most 20 KiB. It watches `Generation`/`GetGeneration`, sends
a fresh baseline at every stream start, clears its local state on stream end, and
retries only while `CanSend` permits. Audience lists must never be sent. This change
provides the channel; world-view controls and rendering are separate consumers.
