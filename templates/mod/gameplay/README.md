# {{DISPLAY_NAME}}

An input-driven gameplay mod ({{MOD_ID}}) with a per-frame controller, modeled on the Gravity Gun mod.

## Quick start

1. Validate the project: `robotopia check package .`
2. Build and package: `robotopia pack`
3. Install into the game: `robotopia install`
4. Play: `robotopia launch` — open the manager with **F10** to see the mod loaded.

## What to edit next

- `{{TYPE_NAME}}Controller.cs` — the per-frame input/physics logic.
- `{{TYPE_NAME}}Config.cs` — user-tunable settings persisted by the SDK.
- `robotopia.mod.json` — declared `input`/`physics`/`hud` permissions; adjust with `robotopia mod add|remove permission <p>`.

New to modding? Follow `docs/YourFirstMod.md` in the QuantumWorks repository; `docs/Modding.md` is the full reference.
