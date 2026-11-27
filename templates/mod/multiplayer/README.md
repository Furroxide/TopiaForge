# {{DISPLAY_NAME}}

A multiplayer mod for Robotopia ({{MOD_ID}}): a count every player in a session shares. In a world, any player
presses **K** to add one. The host owns the count, decides every press and replicates the result, so everyone sees the
same number on their HUD. Solo play, offline included, runs the same code in a one-player session in which you are the
host.

## Quick start

1. Restore the pinned SDK: `topiaforge restore`
2. Generate the contract lock from the built contract: `topiaforge mod sync multiplayer`
3. Build and test: `dotnet build` then `dotnet test tests/{{ASSEMBLY_NAME}}.Tests`
4. Validate and package: `topiaforge check package .` then `topiaforge pack`
5. Install and play: `topiaforge install` then `topiaforge launch`, start a world, and press **K**.

The scaffold starts with an empty `topiaforge.multiplayer.lock.json`, which `topiaforge pack` refuses until step 2
fills it in. Rerun `topiaforge mod sync multiplayer` whenever you change the contract, and commit the lock with
`packages.lock.json` and `topiaforge.sdk.lock.json`: pack rejects a lock that no longer matches, so every wire change
is a reviewed change. The mod depends on the multiplayer provider, `io.github.furroxide.topiaforge.multiplayer`,
at exactly this SDK's version; it ships with TopiaForge and must be installed before this mod.

## How the count is shared

- `[MultiplayerContract(Id = "{{MOD_ID}}")]` names the contract. The id is a permanent wire identity, so keep it
  after release even if you rename the class.
- `[ReplicatedState("count")]` is the count. The host's copy is canonical, a joining player receives it in their first
  snapshot, and every new session starts from the declared default.
- `[MultiplayerCommand("increment", ...)]` is a press. Its request is empty, so a client can only say that it pressed
  and the host decides what that is worth. Owner prediction moves the presser's count at once; a press the host
  refuses rolls back.
- The HUD is made once, in `OnLoad`, and lives as long as the mod. TopiaForge shows it in every world and hides it at
  the main menu and while a scene loads, so the mod handles no scenes. A press counts only while the HUD is on screen.
- The HUD follows the value its own process holds. A press calls `SubmitIncrement`, and the host's answer comes back
  to a callback on the main thread: at once for the host's own player, a round trip later for anyone else, and never
  once the mod has stopped. Nothing waits for the answer, so the game never stalls (TF1008). A refused press shows a
  warning toast, at most one a second, because every mod shares one toast queue.

## Limits, and why

| Declaration | Value | Why |
| --- | --- | --- |
| `MaximumPerSecond` | 10 | About as fast as a person can tap a key, so real presses are rarely refused, while a macro or a modified client cannot flood the host. The host enforces it on its own clock, its own player included. |
| `MaximumPayloadBytes` | 64 | The request is empty and the response is a 4-byte count; 64 is the smallest limit TopiaForge accepts. The host refuses larger payloads before decoding them. |
| Inbound total | 640 B/s | 10 x 64 bytes, about 2 percent of the 32 KiB/s per-connection budget that TFMP014 checks across every command and replicated object in the contract. |

The replicated state declares no rate of its own: changes coalesce into the host's regular updates, at most 10 a
second in a live session, however fast presses arrive.

## Manifest

`topiaforge.mod.json` declares `"multiplayer": {"mode": "session", "presence": "required", ...}`. Every player in a
session needs a compatible copy, because each peer must decode the same contract. Packing adds the contract lock to
the synchronized files, so a change to the contract itself already stops older copies from joining. `protocol.version`
covers what the lock cannot see, such as what a press is worth: it is independent of the package version,
`peerVersionRange` lists the versions this copy plays with, and each side must accept the other. Bump the version, and
the range, when such a change breaks older copies. The `hud` and `input` capabilities disclose the HUD panel and the
key binding.

## What to edit next

- `{{TYPE_NAME}}Mod.cs`: the contract, its limits, the press action and the HUD.
- `CounterPayloads.cs`: the payload types. To send data with a press, add bounded members to `IncrementRequest`
  and check every one in `Increment`, because a client can send any value the codec accepts.
- `tests/`: NUnit tests on the multiplayer test rig, covering the HUD showing in every world and hiding at the main
  menu and during loads, two players agreeing, a late joiner, a press past the rate limit being refused and rolled back,
  one warning for a burst of refusals, no answer once the mod has stopped, a new session, and solo play.

See `docs/Multiplayer.md` and `docs/Diagnostics.md` (the TFMP codes) in the TopiaForge repository.
