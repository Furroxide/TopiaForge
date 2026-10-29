# Persistent cosmetic bodies

TopiaForge 0.2.0 provides `IRobotBodyService` through RobotKit. Obtain it from the shared gamemode server's
context, in single player or on the host. Declare the RobotKit dependency and a loader/SDK floor of 0.2.0.
The service creates a separate powered-down cosmetic body, automatically replicated and owned by that context.
It does not kill a player, trigger native corpse cleanup, damage anything or change the participant's avatar.

```csharp
// Context is the gamemode server's scoped context; player is a current participant.
if (!Context.TryGetExtension<IRobotBodyService>(out var bodies)
    || !player.Appearance.TryGet(out var appearance)
    || !player.TryGetSnapshot(out var snapshot))
{
    // Report that the body cannot be placed yet; do not advance gameplay as if it succeeded.
    return;
}

var result = bodies.Spawn(new RobotBodyRequest(appearance!, snapshot!.Position));
if (!result.TryGetValue(out var body))
{
    // Surface result.ErrorMessage through the mode's existing status/toast flow.
    return;
}

// Keep body only to identify it, inspect its captured values, or release it before the scope ends.
// body.Dispose(); // Idempotent. Also removes every remote replica automatically.
```

The request captures the immutable `AvatarAppearance`: independent opaque plastic and metal colours and the
selected catalog accessory slot. Null accessory retains the loaded provider's default; empty removes the selected
slot. It retains no player, nameplate, participant key or address. Later appearance edits, movement, departure,
avatar teardown and reconnect of the source participant cannot change or remove it. Place it before discarding
the participant's appearance/snapshot; unavailable data must not be replaced with invented values.

Position is the fixed world anchor, with each component finite within ±100,000 metres. Rotation defaults to the
identity; an optional unit quaternion sets the anchor's orientation. The visual lies prone beneath that anchor,
using the same 90-degree pitch and 0.35-metre height offset as participant powered-down presentation. It is a
static mesh snapshot, with no animation, ragdoll, collision, hit target, native conversation or brain. Authors can
use its position for their own nearby-body actions. Nothing runs a native death or corpse timer.

`IRobotBody` is an `IEntity` and `IDisposable`, with captured `Appearance` and `Rotation`. Its local entity id stays
stable for the lease; use the existing runtime entity-reference facilities when a network reference is needed.
Replica ids are runtime-managed, not player ids. Authors register no factory, network object, channel, per-frame
update, scene hook, departure hook or cleanup subscription. Bodies have no timer; explicitly dispose them early
or let the owning scope end. There are no mutable body cosmetics or movement APIs in this contract.

Only a shared gamemode server scope may spawn, including offline. Clients, participant controllers (even the
listen host's), process contexts and non-shared consumers receive `NotAuthoritative`. An ended scope receives
`Cancelled`; a missing seam, provider or world receives `Unavailable`. A native composition failure is a real
failure, with no capsule or invisible-success placeholder. `Conflict` means 64 live bodies in the consuming scope
or 256 in the provider. Explicit disposal returns capacity. A retained facade cannot enter a replacement world.
Provider removal and world replacement end every body it owns, including canonical records and remote replicas.

Replication uses the existing reliable world ledger and provider kind `robotkit.body`. Its immutable payload
contains only the version, powered-down pose and appearance (at most 128 bytes); the descriptor contains the
anchor transform. Late join and transport reconnect receive current bodies from the ordinary baseline. Missed
removals cannot resurrect a lease. Remote scene loss or provider replacement releases old visuals and rebuilds
from the retained canonical description. Recovery inspects at most four provider entries per frame and retries
each failed construction at most once every five seconds. Existing world counters expose unbuildable replicas;
host acceptance is not proof that every client's catalog can render the body immediately.

RobotKit composes a dormant clone under its inactive incubator through the existing exact native cosmetic
bindings. It copies only transforms and rendered meshes, then activates that visual snapshot. Native scripts on
the composition clone never activate. Skinned meshes are baked once and owned until disposal; ordinary meshes
and materials come from the loaded catalog. Bounds are 1,024 transform nodes, depth 64, 256 renderers, 32 material
slots per renderer, 250,000 vertices per body and 4,000,000 vertices across persistent bodies in the provider.
Unknown renderer types, missing meshes/materials, missing bindings or unavailable accessories refuse creation.
No files, URLs, external assets, credentials or arbitrary native type names appear in the request or payload.

For tests, register `FakeRobotBodyService` in a fake context, or use `FakeRobotKit.Bodies`. It captures the same
values, tracks its scope, exposes live bodies and supports authority/provider refusal. Disposal models world or
provider retirement. The production lifecycle and wire tests exercise actual world baseline/despawn/recovery
code with controlled render objects; they do not establish native material, bone, mesh-baking or rig fidelity.
Verify those in the installed game before claiming native visual acceptance.
