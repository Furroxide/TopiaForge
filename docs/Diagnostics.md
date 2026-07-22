---
title: Diagnostics
description: Resolve stable TopiaForge SDK, restore, and development-loop diagnostics.
---

# Diagnostics

TopiaForge errors include a stable code, a plain-language cause, a remediation, and a link to this
reference. Preserve the code when reporting a problem; wording may improve between releases.

## SDK and build diagnostics

### TF1001

**Native API in a safe project.**

The project directly references a Robotopia implementation, Unity implementation, or patching API.
Use the matching `IModContext` service or specialist module. Only an intentionally advanced package
should add `interop-unity` and accept its unstable compatibility policy.

### TF1002

**Unsupported target framework or missing SDK feed.**

Safe mods target `netstandard2.1`. Keep the generated project settings and run
`topiaforge restore` to seed the release-local NuGet source. If the feed is missing, restore it from
the same TopiaForge release rather than adding a source-checkout path.

### TF1003

**Copied SDK assembly or restore failure.**

Framework contract assemblies are reference-only and supplied by the loader. Remove copied SDK
DLLs from the project/package. Keep exact `PackageReference` entries, then rerun
`topiaforge restore` and inspect the first NuGet error if restore still fails.

### TF1004

**Module dependency missing.**

The project references a specialist contract assembly without its manifest/runtime pair. Run
`topiaforge mod add <module>`, then `topiaforge restore`. This adds both the exact contract package
and its root `dependencies` entry; an intentional nonblocking integration may instead use the
root `optionalDependencies` map. Text in descriptions or `x-*` metadata does not count. Commit
`packages.lock.json` and `topiaforge.sdk.lock.json` after restore. Multiplayer projects must also run
`topiaforge mod sync multiplayer` and commit `topiaforge.multiplayer.lock.json`.

### TF1005

**Retired pre-V1 API.**

The project uses an API removed by the V1 reset. Follow the replacement in the analyzer message:
derive from `TopiaForgeMod`, use typed context properties, and use dependency-scoped extensions.
There is no compatibility shim.

### TF1006

**Required capability missing.**

The project references an API whose risk must be disclosed by a canonical root manifest
capability. `TopiaForge.Mods.Interop.Unity` requires `unsafe-native` in the root `capabilities`
array. Remove the interop package and use safe SDK services when possible; otherwise add the
capability and review the unstable compatibility and full-process-trust implications. Matching
text in descriptions or `x-*` metadata does not count.

### TF1007

**Loader-owned renderer referenced by a safe project.**

`TopiaForge.Mods.UnityUi` is an internal Robotopia-runtime renderer, not an authoring package. Remove
the reference and express HUDs, windows, modals, toasts, and accessibility preferences through
`Context.Ui`. Declaring `unsafe-native` does not authorize coupling a mod to the loader-owned
renderer.

### TF1101

**Interop references unavailable.**

The explicitly unstable interop package requires Robotopia managed reference assemblies. Install
Robotopia and run `topiaforge restore`, or remove the package and use the safe SDK.

## Runtime diagnostics

Use `Context.Runtime.UnavailableCapabilities` to explain why a Robotopia binding or provider feature is
not active. Use `Context.Diagnostics.Report(...)` for structured, bounded reports that belong in a
diagnostic bundle; use `Context.Logger` for ordinary attributed operational messages.

After a run, `last-run.json` records exact packages and hashes, authoritative load order, stage
timings, compatibility decisions, outcomes, root exception chains, and recovery state. Installation
receipts detect modified package bytes before execution. Startup recovery quarantines a mod only
when the journal proves the process stopped inside that mod's load callback. Any other unclean exit
uses one-shot safe mode without assigning blame; only an explicit clean-exit record skips recovery.

## Development-loop diagnostics

The `topiaforge dev` command stops at the first failed stage and never installs an unvalidated
package. Each code below names exactly one stage.

### TFDEV100

**Restore failed.** Run `topiaforge restore --project <path>` and resolve every reported lock, feed,
or SDK issue.

### TFDEV105

**Pinned toolchain unavailable.** Install the exact SDK in `global.json`, then restore again.

### TFDEV110

**Build failed.** Fix the first compiler/analyzer diagnostic, then run `dotnet build --no-restore`.

### TFDEV120

**Tests failed.** Run `dotnet test --no-restore` for the complete test-host and assertion output.

### TFDEV130

**Pack failed.** Check the manifest, entry assembly, declared package files, and Release output.

### TFDEV140

**Package validation failed.** Run `topiaforge check package <archive>` and correct every blocking
manifest, managed assembly, or archive finding.

### TFDEV150

**Install failed.** Pass `--game-dir <Robotopia folder>` or set `ROBOTOPIA_GAME_DIR`, then run
`topiaforge doctor`.

### TFDEV160

**Launch failed.** Run `topiaforge doctor`, repair the runtime in the launcher, and retry with
`--no-tail` while diagnosing.

### TFDEV170

**Log tail failed.** Inspect `BepInEx/TopiaForge/logs` and the launcher diagnostic bundle, or rerun
with `--no-tail`.

See [Development loop](CliDevLoop.md) for flags, interactive defaults, and generated project state.

## Release scaffold diagnostics

### TFSCF170

**The scaffold or its installed package evidence is not release-portable.** Run
`topiaforge restore --project <path>`, then rerun `topiaforge check scaffold <path>`. Correct every
reported SDK lock, project reference, exact package version, generated props, install receipt, or
manager state mismatch. A project must remain buildable after the release extraction is removed.

### TFSCF171

**The independent scaffold validator could not complete.** Repair or re-extract the matching
TopiaForge release so the CLI, pinned `global.json`, and `tools/package-validator` directory remain
together. Retry the command after confirming the pinned .NET SDK is installed.

### TFPKG150 / TFPKG151

**The trusted metadata validator or its pinned `global.json` is missing.** Repair or re-extract the
TopiaForge release; do not copy the CLI executable away from its `tools/package-validator` support
directory.

### TFPKG160 / TFPKG161

**Managed package metadata is invalid or could not be inspected.** Run `topiaforge check package
<archive>` and fix the reported PE, identity, target-framework, entry-type/constructor, SDK, API, or
bundled-framework finding. Validation reads metadata only and never loads mod code.

### TFPKG170

**Downloaded package metadata differs from the manifest approved during planning.** Refresh the
package source and retry. Source maintainers must publish immutable bytes whose canonical V5
manifest exactly matches the indexed manifest; TopiaForge does not stage mismatched packages.

### TFINBOX100–TFINBOX130

**A package-inbox safety or installability check failed.** The affected archive is retained rather
than executed or silently deleted. Fix the reported Robotopia-installation, size, manifest, runtime,
dependency, metadata, hash, or filesystem issue and process the inbox again. Files changed during
processing remain in a `.topiaforge-consume-*` quarantine directory under the inbox for inspection.

### TFLOG001

**A completed operation could not be recorded in the launcher file log.** The durable operation is
still successful. Check that the launcher data `logs` directory is writable, is not a symbolic link,
and has free disk space; the bounded fallback diagnostic is written to the launcher's standard error
stream.
