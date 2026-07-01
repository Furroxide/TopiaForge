# QuantumWorks Creator Companion Workflow

The QuantumWorks developer workflow adapts product ideas from the VRChat Creator
Companion and VPM package flow to Robotopia's BepInEx/.NET mod runtime. VCC is
used as clean-room product reference only; no VCC code, prose, or assets are
copied into this repository.

Reference: https://github.com/vrchat-community/creator-companion

## Project Files

Developer projects use source-control-friendly files:

- `robotopia.project.json`: project id, name, dependency ranges, package
  sources, and optional Unity companion settings.
- `robotopia.lock.json`: resolved package versions, source URLs, hashes,
  dependency graph, and exported API assemblies.
- `robotopia.dev.props`: generated MSBuild references for dependency API
  assemblies. Do not commit this file.

Generated package caches live under `.robotopia/packages/` and are also ignored.

## CLI

The Dart CLI package lives in `apps/robotopia_cli` and exposes the `robotopia`
executable.

Common commands:

```powershell
dart run apps\robotopia_cli\bin\robotopia.dart new mod author.example --name "Example Mod"
dart run apps\robotopia_cli\bin\robotopia.dart add package robotopia.worlds@1.x
dart run apps\robotopia_cli\bin\robotopia.dart restore
dart run apps\robotopia_cli\bin\robotopia.dart pack
dart run apps\robotopia_cli\bin\robotopia.dart doctor
```

`restore` resolves dependencies from configured package sources, verifies
SHA-256 hashes when supplied, extracts packages into `.robotopia/packages/`, and
generates `robotopia.dev.props` so C# code can compile against exported APIs.

## Exported C# APIs

Runtime-only dependencies belong in `dependencies` or `optionalDependencies`.
If a package intentionally exposes C# APIs for other mods to compile against,
list those DLLs in `apiAssemblies`:

```json
{
  "apiAssemblies": ["ref/Robotopia.Worlds.Api.dll"]
}
```

Only `apiAssemblies` are written into `robotopia.dev.props`. This keeps runtime
load ordering separate from compile-time API contracts.

## Package Sources

The launcher and CLI read the existing flat `mods` registry and the
`packages -> versions` repository shape used by VPM-style indexes. VPM's
`vpmDependencies` field is accepted as an alias for Robotopia dependency ranges
when a source can be mapped to `robotopia.mod.json` metadata.

## Unity Companion

Unity is optional for Robotopia code mods. The CLI and launcher can detect Unity
Hub/Editor for AssetBundle authoring workflows, and new projects may include an
optional `unity-companion` folder. The launcher does not install Unity in v1.
