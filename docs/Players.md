---
title: Act on players
description: Use the same player handles for solo and multiplayer gameplay, targeted UI, input and scores.
---

# Act on players

Resolve `Context.RequireMultiplayer().Players` once. Its `Players` list contains exactly one player in solo play and
all participants online. Use it on the game's main thread. Keep a handle for as long as its player is present;
`SubscribeJoined` and `SubscribeLeft` report changes. Existing players are available from the list and are not replayed
to a new subscription.

```csharp
var players = Context.RequireMultiplayer().Players;
foreach (var player in players.Players)
{
    player.Heal(25, "round-start");
}
```

Server gameplay can damage, heal or teleport any player. The runtime sends the request to that player's own game and
accounts for authorized heals and placements in the host's presence checks. A joined client can act on itself, but an
attempt to change another player returns `NotAuthoritative`. Check `OperationResult.Succeeded` when an action matters:
`Unavailable` means the world or connection cannot receive it yet, and `NotFound` means the handle has retired.
For a remote health change the immediate result is an estimate; `TryGetHealth` subsequently reads the confirmed pose.

Actions, windows and input travel between games under your mod's id, so reaching another player needs a mod that every
player runs: one with no `multiplayer` block, or a required `session` mod. A `client-local` or `server-only` mod reaches
only its own game's player. For anyone else its actions and UI return `Unavailable` with a message that says so, and
their `Input` reads stay false; reads such as `TryGetSnapshot`, `TryGetHealth` and `Entity` still work.

`TryGetSnapshot` reads position and aim; `TryGetHealth` reads current and maximum health. Either can return false while
the world loads. `Entity` is the local player entity or the remote presence avatar, suitable for an entity target.
Repeated unchanged reads allocate nothing after the first read.

An `IPlayer` never changes identity. A reconnect that retains the host's participant record keeps its handle. A leave,
replacement key, or participant-list generation change retires it permanently, along with retained `Input` and `Ui`
references. A new host reusing the same participant id does not revive it.

## A window for one player

```csharp
var window = player.Ui.CreateSurface(new UiSurfaceRequest(
    "shop", "Shop", "Choose a reward.",
    content: new UiButton("heal", "Restore health", () => player.Heal(25, "shop"))));
```

For a remote player, only that player sees the window. The callback stays on the host; a reply carries a control id and
the control's value. The runtime checks the sender, target handle, window revision, visibility and value before running
it. Replacing content revokes the old controls. Disposing the surface or its owning scope revokes every callback.
Windows and unanswered modals are recreated automatically after reconnecting to the same session.

Use `ShowModal` for confirmation and `ShowToast` for action results. A modal reports exactly one answer, as a local one
does: the player's choice, or `false` when it is closed or disposed, its player leaves, or its scope or the session
ends. A callback that throws is logged to your mod's log, as a local UI callback is, and every other reply still runs.

Remote text is literal, including titles and control labels. Graph canvases become a text placeholder. HUDs remain
noninteractive. Compositions use the ordinary `UiComposition` limits, with a 32 KiB wire limit, 4096-character text
fields, 512-character toasts, and bounded window dimensions. The local player's UI is the ordinary `Context.Ui`, with
its own rules. A request that does not fit returns an error; keep long content in a scroll or virtual list.

## Input and scores

Register actions through `Context.Input.RegisterAction` as usual. Read `player.Input.IsHeld("fire")`,
`WasPressed("fire")` and `WasReleased("fire")` in a canonical tick. Each player's registered action names belong to the
owning mod; the authenticated sender supplies the player identity. Up to 64 actions are sampled. Press and release
edges last one tick, a short tap preserves both edges, and a silent action releases after 2.5 seconds. Input is sampled
automatically and sent within a per-player budget. Clients supply their own input; gameplay still validates what an
action is allowed to do.

The host can register a lifetime-owned column and set a display value and numeric sort key:

```csharp
var score = players.RegisterScoreboardColumn(new ScoreboardColumnDefinition("score", "Score")).Value;
score?.SetValue(player.Id, "12", 12);
```

Values reach every player, including late joiners and reconnects. Departed handles lose their values. Display values
are at most 32 characters. There are at most 64 columns and a bounded total payload; values that exceed that budget
return `RateLimited`. The scoreboard overlay consumes this data in its own delivery slice.

## Test the same logic

```csharp
using var players = new FakePlayerDirectory();
var sam = players.Add(new ParticipantId("p1"), "Sam");
sam.Damage(new PlayerDamageRequest(20, "enemy"));
sam.TryGetHealth(out var health); // 80 / 100
players.State(sam.Id).Input.SetHeld("fire", true);
var fired = sam.Input.WasPressed("fire"); // true for the rest of this tick
players.AdvanceTick(); // the press edge ends; "fire" stays held
players.Remove(sam.Id); // sam now stays retired
```

The fake exposes each player's local services, captured UI and simulated input. `ReplaceGeneration` exercises a new
participant list with reused ids and keys. `IsAuthoritative = false` exercises refused remote actions and score writes.
`TryGetScore` inspects display values and sort keys, and `CallbackErrors` lists what join and leave handlers threw.
Import `TopiaForge.Mods` and `TopiaForge.Mods.Testing` in these tests.
