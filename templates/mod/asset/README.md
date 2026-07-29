# {{DISPLAY_NAME}}

An asset content mod ({{MOD_ID}}) that ships Unity AssetBundles and loads them through the
owner-bound `Context.Assets` SDK service.

## Quick start

1. Author content in the scaffolded `unity-companion/` Unity project and build its AssetBundles.
2. Restore and test: `topiaforge restore` then `dotnet test tests/{{ASSEMBLY_NAME}}.Tests`.
3. Validate the project: `topiaforge check package .`
4. Build and package: `topiaforge pack`; install and play with `topiaforge install` then `topiaforge launch`.

## What to edit next

- `{{TYPE_NAME}}Mod.cs` — set `PrefabName` to the prefab's asset path inside your bundle. The sample
  performs a real asynchronous load and spawn, reports stable SDK errors, and needs no Unity API.
- `unity-companion/` — the paired Unity project where bundles are authored.
- `tests/` — a real NUnit test covering load, prefab spawn, and lifetime cleanup with no Unity installation.

New to modding? Follow `docs/YourFirstMod.md` in the TopiaForge repository; `docs/Modding.md` covers asset bundles.
