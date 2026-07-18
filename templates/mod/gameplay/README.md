# {{DISPLAY_NAME}}

An input-driven gameplay mod ({{MOD_ID}}) that registers a named, rebindable action and raycasts
along the player's center-screen aim. The default key is **G**; it is intentionally not a framework hotkey.

## Quick start

1. Restore the pinned SDK: `topiaforge restore`
2. Build and test: `dotnet build` then `dotnet test tests/{{ASSEMBLY_NAME}}.Tests`
3. Validate and package: `topiaforge check package .` then `topiaforge pack`
4. Install and play: `topiaforge install` then `topiaforge launch`; press **G** while aiming.

## What to edit next

- `{{TYPE_NAME}}Controller.cs` — named-input, player aim, raycast, logging, and toast behavior.
- `{{TYPE_NAME}}Config.cs` — validated action key and raycast range.
- `tests/` — a real NUnit aim/raycast test with deterministic SDK fakes.
- `topiaforge.mod.json` — V4 capabilities and compatibility ranges.

New to modding? Follow `docs/YourFirstMod.md` in the TopiaForge repository; `docs/Modding.md` is the full reference.
