# {{DISPLAY_NAME}}

An asset content mod ({{MOD_ID}}) that ships Unity AssetBundles and loads them via the Assets service.

## Quick start

1. Author content in the scaffolded `unity-companion/` Unity project and build its AssetBundles.
2. Validate the project: `robotopia check package .`
3. Build and package: `robotopia pack`
4. Install and play: `robotopia install` then `robotopia launch`.

## What to edit next

- `{{TYPE_NAME}}Mod.cs` — loads bundles through the `robotopia.assets` service (declared in `vpmDependencies`).
- `unity-companion/` — the paired Unity project where bundles are authored.

New to modding? Follow `docs/YourFirstMod.md` in the QuantumWorks repository; `docs/Modding.md` covers asset bundles.
