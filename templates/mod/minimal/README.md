# {{DISPLAY_NAME}}

A minimal TopiaForge SDK mod for Robotopia ({{MOD_ID}}) that loads validated typed config,
migrates older settings, logs lifecycle information, and registers a `greet` command.

## Quick start

1. Restore the pinned SDK: `topiaforge restore`
2. Build and test: `dotnet build` then `dotnet test tests/{{ASSEMBLY_NAME}}.Tests`
3. Validate and package: `topiaforge check package .` then `topiaforge pack`
4. Install and play: `topiaforge install` then `topiaforge launch`

## What to edit next

- `{{TYPE_NAME}}Mod.cs` — the entry point and owner-scoped `greet` command.
- `{{TYPE_NAME}}Config.cs` — defaults, validation, and the V1-to-V2 migration.
- `tests/` — a real NUnit lifecycle test using `TopiaForge.Mods.Testing`.
- `topiaforge.mod.json` — describe the mod (`topiaforge mod set <field> <value>` keeps it valid).

New to modding? Follow `docs/YourFirstMod.md` in the TopiaForge repository; `docs/Modding.md` is the full reference.
