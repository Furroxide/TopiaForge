---
title: Editor companion packages
description: Manage optional editor-side content tooling separately from V1 mod dependencies.
---

# Editor companion packages

Editor companion packages are optional authoring tools for bundle-backed UGC and custom worlds.
They are not TopiaForge code-mod dependencies and are never loaded into Robotopia as mod assemblies.

Use `topiaforge unity` commands and the Creator Companion project UI to add, remove, resolve, and
update editor packages. The tool owns the editor package manifest and lock state; generated projects
should not hand-edit resolver metadata.

The embedded project-open recovery bridge only verifies the exact locked packages already on disk.
It never reads package listings, downloads archives, or changes `Packages/`; when recovery is needed,
it copies the explicit `topiaforge unity resolve` command for the author to run.

V1 `.topiaforgemod` runtime dependencies always use the canonical `dependencies` and
`optionalDependencies` maps in `topiaforge.mod.json`. Add code-mod specialist modules with
`topiaforge mod add <module>` so compile-time contracts and runtime dependencies remain paired.

## Trust boundary

Editor package listings, archives, and manifests are untrusted input. TopiaForge requires HTTPS for
remote sources, pins SHA-256, bounds downloads/extraction, rejects unsafe paths and ambiguous
duplicates, and resolves exact lock state before the editor opens a project.

An editor package may generate content, but it must not inject Robotopia-side mod code or copied SDK
assemblies. Published player content is repacked and validated through the ordinary
`.topiaforgemod` pipeline.

See [UGC live sync](UgcLiveSync.md), [Custom worlds](CustomWorlds.md), and
[Manifest V4](ManifestV4.md#dependencies-and-ordering).
