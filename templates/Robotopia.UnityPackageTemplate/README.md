# Robotopia / QuantumWorks Unity package template

A starter QuantumWorks **VPM** Unity package (the equivalent of VRChat's `template-package`).

Scaffold one with `robotopia unity new-package <com.you.your-package> [--name "Display Name"] [--dir path]`, then:

1. Put always-included code in `Runtime/`, editor-only tooling in `Editor/`, and optional sample assets in `Samples~/`.
2. Edit `package.json` (`name`, `version`, `displayName`, `unity`, `vpmDependencies`, `samples`).
3. Publish to a listing with `tools/pack-unity-packages.ps1`, which zips every `com.robotopia.*` package and
   regenerates `dist/vpm/index.json`. Add that listing to projects via `robotopia unity add-repo <index.json>`.

See `docs/UnityVpm.md` in the QuantumWorks repo for the package/manifest/listing formats and the resolver.
