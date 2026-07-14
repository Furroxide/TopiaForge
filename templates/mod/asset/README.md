# {{DISPLAY_NAME}}

An asset content mod ({{MOD_ID}}) that ships Unity AssetBundles and loads them via the Assets service.

## Quick start

1. Author content in the scaffolded `unity-companion/` Unity project and build its AssetBundles.
2. Validate the project: `topiaforge check package .`
3. Build and package: `topiaforge pack`
4. Install and play: `topiaforge install` then `topiaforge launch`.

## What to edit next

- `{{TYPE_NAME}}Mod.cs` — loads bundles through the `io.github.furroxide.topiaforge.assets` service (declared in `vpmDependencies`).
- `unity-companion/` — the paired Unity project where bundles are authored.

New to modding? Follow `docs/YourFirstMod.md` in the TopiaForge repository; `docs/Modding.md` covers asset bundles.
