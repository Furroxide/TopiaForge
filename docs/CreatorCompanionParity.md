# TopiaForge Creator Companion Workflow

The TopiaForge developer workflow adapts product ideas from the VRChat Creator
Companion and VPM package flow to Robotopia's BepInEx/.NET mod runtime. VCC is
used as clean-room product reference only; no VCC code, prose, or assets are
copied into this repository.

Reference: https://github.com/vrchat-community/creator-companion

## Project Files

Developer projects use source-control-friendly files:

- `topiaforge.project.json`: project id, name, dependency ranges, package
  sources, and optional Unity companion settings.
- `topiaforge.lock.json`: resolved package versions, source URLs, hashes,
  dependency graph, and exported API assemblies.
- `topiaforge.dev.props`: generated MSBuild references for dependency API
  assemblies. Do not commit this file.

Generated package caches live under `.topiaforge/packages/` and are also ignored.

## CLI

The Dart CLI package lives in `apps/topiaforge_cli` and exposes the `topiaforge`
executable.

Common commands (these examples assume the `topiaforge` executable from the release zip is on `PATH` — see
[Modding.md → Install the CLI](Modding.md#install-the-cli); from a source checkout use
`dart run topiaforge <command>` inside `apps/topiaforge_cli`):

```powershell
topiaforge new mod author.example --name "Example Mod"
topiaforge add package io.github.furroxide.topiaforge.worlds@1.x
topiaforge restore
topiaforge pack
topiaforge doctor
```

`restore` resolves dependencies from configured package sources, verifies
SHA-256 hashes when supplied, extracts packages into `.topiaforge/packages/`, and
generates `topiaforge.dev.props` so C# code can compile against exported APIs.

## Exported C# APIs

Runtime-only code-mod dependencies belong in the schema-V4 `dependencies` or
`optionalDependencies` maps. Editor package dependencies remain isolated in editor project state.
If a package intentionally exposes C# APIs for other mods to compile against,
list those DLLs in `apiAssemblies`:

```json
{
  "apiAssemblies": ["ref/TopiaForge.Worlds.Api.dll"]
}
```

Only `apiAssemblies` are written into `topiaforge.dev.props`. This keeps runtime
load ordering separate from compile-time API contracts.

## Package Sources

The launcher and CLI read the existing flat `mods` registry and the
`packages -> versions` repository shape used by VPM-style indexes. TopiaForge
uses editor-package identity, display, author, and dependency fields in
`topiaforge.mod.json` while keeping `.topiaforgemod` as the runtime package file.

## Unity Companion

Unity is optional for TopiaForge code mods. The CLI and launcher can detect Unity
Hub/Editor for AssetBundle authoring workflows, and new projects may include an
optional `unity-companion` folder. The launcher does not install Unity in v1.
