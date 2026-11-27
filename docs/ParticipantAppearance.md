# Participant appearance

TopiaForge 0.2.0 adds `IPlayer.Appearance` for host-controlled participant cosmetics. The API works through the
gamemode server's scoped player directory, in single player and multiplayer. It uses the existing presence avatars
and RobotKit cosmetic body provider. It never changes a player's identity, display name in the directory, health,
input, collision or movement authority.

```csharp
// Obtain players from the server context's IMultiplayerSession.Players directory.
var appearance = new AvatarAppearance(
    plastic: new RgbaColor(0.85f, 0.15f, 0.1f),
    metal: new RgbaColor(0.2f, 0.25f, 0.3f),
    accessory: "", // Remove the hat. Null retains the provider's default accessory.
    accessorySlot: AvatarAccessorySlot.Hat);

var changed = player.Appearance.Set(appearance);
var named = player.Appearance.SetNameplate("Sam", visible: true);
var hidden = player.Appearance.SetNameplate(visible: false);
var poweredDown = player.Appearance.SetPoweredDown();
var disguise = player.Appearance.CopyFrom(otherPlayer);

// Check each OperationResult. Keep a successful lease only when it should end early.
if (disguise.TryGetValue(out var temporaryAppearance))
    temporaryAppearance.Dispose(); // Restore this player's underlying appearance.
// Ending the owning context automatically disposes every remaining lease.
```

Plastic and exterior metal take independent finite, opaque RGB colours in the range zero through one. Accessories
name a prefab from the loaded robot composer's catalog, with exact case, at most 64 printable ASCII characters and
no slash, backslash or colon. Supported cosmetic slots are Hat, Glasses, Hair, Beard and Necklace. Null keeps the
default accessory; an empty string removes that slot. The API never loads a file, URL or arbitrary asset bundle.
Authors choose colours, an optional catalog name, and when the effect ends; they write no transport, catch-up,
avatar lookup, render loop or cleanup code. Catalog availability depends on the loaded game content.

Every successful call returns an automatically tracked `IPlayerAppearanceLease`. Latest live lease wins separately
for appearance, nameplate and powered-down pose. Disposal can happen in any order: ending an older lease cannot
replace a newer one, and ending the newer one cannot resurrect an already-ended lease. At most 16 leases may exist
per participant across contexts; dispose temporary effects when finished. `Conflict` reports that bound. Mutations
from clients, participant controllers (including the host's participant controller), or online client-local and
standalone contexts fail with `NotAuthoritative`. Retained handles fail once their player leaves or scope ends.

`CopyFrom` snapshots the source's effective appearance at the time of the call. It requires a current player from
the same scoped directory. It does not follow later source edits or copy the source's nameplate, identity or pose.
`TryGet` reads the effective cosmetic appearance, including on clients after the host baseline arrives. It returns
false before that state is available, while the participant is disconnected, or after its context ends.

Nameplates accept literal single-line text of at most 32 characters. Null restores the current participant display
name. The existing address redactor runs before publication and the automatic nametag overlay renders text literally.
Showing and hiding change only that overlay; roster and chat identity remain intact. A hidden plate can still have
its text changed. No author-created HUD or per-frame nametag update is needed.

Powered-down is a deterministic prone cosmetic pose with its animation stopped. It does not kill, stun, immobilize
or persist a corpse. Presence still follows the participant's authoritative position. A player's own game remains
first-person, with no extra self-avatar or self-nameplate; its effective state is still delivered locally. Other
players see that player's body and nameplate through presence. Persistent, separately spawned bodies are a separate
API concern.

The host retains effective state through a transport reconnect within the retained participant's grace period.
A departure, changed player key, session replacement or stopped scope retires its changes. New avatars after scene
changes receive the current appearance. Late joiners receive the effective state automatically. Scope disposal
restores the original presence palette, default accessory, ordinary nameplate and standing pose on every client,
including a client that reconnects after missing cleanup.

The runtime publishes only changed records, with a maximum of four per recipient per 100 ms on the reliable avatars
channel. Each record is at most 2,048 bytes and binds the session, participant, host-scoped player key and connection
lifetime. A blocked recipient is retried without blocking others; superseded edits coalesce before the next send.
Clients accept only their authenticated host's records and apply a complete validated record atomically. Reads and
idle send/draw work allocate nothing after warmup. There is no new endpoint or address-bearing SDK surface.

A successful result means the host accepted the presentation request, not that every renderer has already displayed
it. The existing capsule fallback remains when RobotKit, its catalog entry or a native binding is unavailable.
The capsule uses the plastic colour and prone pose; metal and accessories require a cosmetic body. Failures produce
a bounded warning without native exception messages, and the provider is retried on the existing five-second cadence.
Native visual validation is still required for the installed game's robot materials, accessories and animation rigs.
