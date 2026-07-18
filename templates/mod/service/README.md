# {{DISPLAY_NAME}}

A framework service mod ({{MOD_ID}}) that publishes a typed, dependency-scoped service other mods consume
through `RequireExtension<I{{TYPE_NAME}}Service>()`.

## Quick start

1. Validate the project: `topiaforge check package .`
2. Build and package: `topiaforge pack`
3. Install into the game: `topiaforge install`
4. Play: `topiaforge launch` — open the manager with **F10** to see the mod loaded.

## What to edit next

- `contracts/{{ASSEMBLY_NAME}}.Api/` — the separately compiled public contract other mods reference. Only
  `{{ASSEMBLY_NAME}}.Api.dll` is exported through `apiAssemblies`; implementation details stay private.
- `{{TYPE_NAME}}Service.cs` — the implementation registered at load.
- `{{TYPE_NAME}}ConsumerExample.cs` — the compiled consumer pattern. Consumer manifests must declare
  `{{MOD_ID}}` as a dependency before resolving the extension.
- `tests/{{ASSEMBLY_NAME}}.Tests/` — an NUnit lifecycle test using `TopiaForge.Mods.Testing`; run it with `dotnet test`.

Registrations are owned by the mod lifetime and disappear automatically after unload or a failed load.
Keep public consumer-facing types in the contract project. Keep the mod entry point and service implementations
in the root project so changing implementation details does not accidentally expand your public API.
New to modding? Follow `docs/YourFirstMod.md` in the TopiaForge repository; `docs/Modding.md` covers services and `apiAssemblies`.
