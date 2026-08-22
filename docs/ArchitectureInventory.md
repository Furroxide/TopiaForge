# TopiaForge ecosystem inventory

This is the release-facing component and contract map for the complete repository. It records ownership boundaries,
not merely build projects. Update it whenever a public contract, generator, or release payload changes.

## Runtime and C# SDK

| Component | Responsibility | Direct boundaries / outputs |
| --- | --- | --- |
| `TopiaForge.ModManager.Core` | Manifest, version, dependency, path, state, package, and profile-domain logic | Unity-free `netstandard2.1`; consumed by the BepInEx runtime and C# tests |
| `TopiaForge.ModManager` | BepInEx plugin, startup/shutdown, runtime install state, mod loading/isolation, scenes, logs, package inbox, manager overlay | May reference Unity/BepInEx; ships as the Robotopia-side loader |
| `TopiaForge.Mods.Abstractions` | V1 safe authoring contracts and manager-owned core services | Unity-free `netstandard2.1`; AssemblyVersion remains `1.0.0.0` throughout V1 |
| `TopiaForge.Mods.Chronos`, `.CreatorContent`, `.Multiplayer`, `.Prompts`, `.RobotKit`, `.Worlds` | Optional specialist contract modules | Unity-free reference packages coupled to runtime dependencies by `topiaforge mod add`; Multiplayer is a stable API preview with loopback only |
| `TopiaForge.Mods.Multiplayer.Generators` | Multiplayer codecs, registration, protocol descriptors, and prediction-safety diagnostics | Compile-time analyzer package; no transport or native engine surface |
| `TopiaForge.Mods.Testing` / `.Analyzers` | Runner-neutral fakes/lifecycle harness and safe-project diagnostics | Packaged with every SDK release; generated tests use NUnit |
| `TopiaForge.Mods.Interop.Unity` | Explicitly unstable native escape hatch | Requires `unsafe-native`; excluded from V1 compatibility guarantees and normal templates |
| `TopiaForge.Mods.UnityUi` | Loader-owned TopiaForgeUi renderer, themes, allocator, widgets, motion, accessibility, and embedded brand bundle | Unity-only provider implementation; not an authoring package or V1 compatibility contract; ordinary mods use `Context.Ui` |
| `TopiaForge.GameCompat.Surface` | Serializable Robotopia-code surface and comparison rules | Unity-free contract shared by extractor/tests |
| `TopiaForge.GameCompat.Extractor` | Metadata-only installed-Robotopia inspection | Self-contained developer/release executable; never loads Robotopia code for execution |
| `TopiaForge.ModManager.Tests` | Cross-component C# harness | Exercises Core, SDK, GameCompat, runtime source conventions, and pure mod seams |

The canonical Robotopia-side loader payload contains fourteen managed assemblies: twelve
`TopiaForge.*` implementations/contracts plus pinned `System.Reflection.Metadata`
and `System.Collections.Immutable` 10.0.9. Robotopia build 2309 supplies the
required `System.Memory`, `System.Buffers`, and
`System.Runtime.CompilerServices.Unsafe` Unity/Mono profile assemblies; release
tests verify their exact identities and hashes instead of shadowing them in the
plugin directory. Launcher repair and CLI release packaging consume the same
inventory from `launcher_data`.

The primary solution builds sixteen first-party mods: Chronos, CreatorContent, CreatorTools, GravityGun, Multiplayer,
NoFeedbackUrl, OppositeDay, PerfFixes, Performance, Prompts, RobotKit, Sandbox, UiGallery, Worlds, and
Zombies. Assets are now a manager-owned core service,
not a globally mutable framework mod. Runtime dependencies are expressed only through `topiaforge.mod.json`;
project references to safe contracts are compile-time-only. UiGallery is a validated developer catalog and is excluded
from the fourteen-package normal non-DevTool payload and the fifteen-package release payload.

## Launcher and developer tooling

| Component | Responsibility | Allowed dependencies |
| --- | --- | --- |
| `launcher_domain` | Immutable models, SemVer/ranges, manifests, profiles, dependency/install planning, registry contracts | Dart only; no Flutter, filesystem, network, archive, or process APIs |
| `launcher_data` | Local repositories and services for storage, downloads, archives, processes, runtime repair, diagnostics, and Unity/VPM tooling | Depends on `launcher_domain`; returns domain-ready typed data |
| `launcher_ui` | Shared Flutter theme, motion, and presentation widgets | Flutter only; no application state or data access |
| `topiaforge_launcher_flutter` | Desktop application and `LauncherBloc` event/state coordination | Depends on all launcher packages; widgets dispatch events and never perform data I/O |
| `topiaforge` CLI | Mod/template/registry/VPM/world/UI/release commands and deterministic packaging | Reuses domain/data services; is not a second implementation of archive or process policy |

## Package and serialization contracts

| Contract | Version / compatibility rule | Producers and consumers |
| --- | --- | --- |
| `.topiaforgemod` ZIP + `topiaforge.mod.json` | Manifest V5 is the sole 1.0 schema; omitted multiplayer metadata means standalone-only, while an explicit block opts into bounded protocol/content metadata; retired V4 is rejected with migration guidance | CLI/scaffolds/first-party builds produce; launcher and runtime dispatch/validate/consume |
| SemVer and version ranges | SemVer 2.0 precedence; exact, wildcard, and comparator-set ranges | C# Core and Dart domain must pass shared parity fixtures |
| Robotopia build version | Numeric build `N` maps to `0.0.N`; initial release is exactly `0.0.2309` | Extractor/runtime detect; launcher plans; manifests constrain |
| Manager/profile/session state | Versioned, normalized, bounded, atomic, and strict; installed versions coexist, exact profile pins fail closed, and unpinned profiles select the highest compatible SemVer | Launcher data writes; runtime reads process-scoped session state |
| Registry entry/index | Format 2, append-only published history, HTTPS + SHA-256 | CLI builds/validates; launcher data consumes as untrusted input |
| World and TopiaForgeUi bundle manifests | Exact Unity `6000.0.23f1`, target, inputs, and SHA-256 provenance | Unity batch builders produce; CLI/package/runtime validate |
| Release policy/BOM/catalog | Product/component versions, signing trust, expected artifacts, and local handoff evidence are checked against source metadata; stable Pages metadata remains manual-only | Admin orchestrator and CLI produce; protected GitHub finalizer verifies |
| Launcher update metadata V1 | Ed25519-signed exact UTF-8 payload with immutable GitHub asset URLs, hashes, sizes, entry inventory, and complete install layouts | Protected GitHub finalizer signs after verifying admin-built bytes; launcher verifies before parsing and reconciles with GitHub |

## Templates and authoring surfaces

- Seven C# mod templates: minimal, gameplay, gamemode, service, UI, asset, and world.
- Unity world project template with the world companion and embedded VPM resolver.
- Standalone Unity package template.
- TopiaForgeUi bundle source project under `tools/unity-ui-bundle`.
- Three first-party VPM packages/listings generated and validated by the CLI.

Templates are release inputs, not documentation snippets: every template is scaffolded, built, packed twice, and
validated from its resulting archive. Default scaffolds adopt the project license and stay non-publishable until
author identity is supplied.

## Compatibility, registry, and repository support data

- `baselines/gamecode.surface.baseline.json` is the reviewed build-2309 compatibility surface. The extractor may
  propose an update, but release validation rejects an unexplained or different-build baseline.
- `bindings/*.gamebindings.json` are the nine first-party provider/advanced-mod runtime binding declarations
  consumed by the compatibility audit. Safe consumer mods such as GravityGun, OppositeDay, Sandbox, and Zombies have no binding
  manifest because they use SDK contracts only. Binding declarations are contract inputs, not generated success
  evidence; dynamic/value bindings still require Robotopia runtime QA.
- `registry/` intentionally contains no official community entries for v1. Its README and the format-1 CLI commands
  support local and self-hosted sources while submissions are closed.
- `third_party/BepInEx/` contains the pinned 5.4.23.5 Windows/macOS archives, extracted payloads, license texts, and
  immutable provenance consumed by runtime repair and release packaging.
- `.githooks/` contains repository-owned commit/push/LFS validation hooks; `.vscode/` selects the project FVM SDK and
  recommends editor integrations. Neither is a runtime dependency.
- `data/` is reserved and currently has no tracked release input. `build/` and `dist/` are generated/ignored work
  areas and must never be treated as authoritative source or silently reused by a clean release build.

## Generated and release artifacts

- Ignored managed Robotopia references and `Directory.Build.local.props` are generated from the exact hashed Robotopia build
  pin; proprietary assemblies are never committed or released.
- Flutter platform registrants, lock files, Unity `.meta` files, bundle manifests, prefab assets, and VPM listings are
  generated artifacts whose source/provenance must agree with their generators.
- The RC1 candidate consists of one canonical deterministic ecosystem payload plus Windows x64 and Linux x64
  platform archives. Nested mod/VPM hashes must be identical between the two archives. Generic macOS packaging
  remains in source for a later release but is not part of RC1's exact asset inventory.
- Production Windows bytes are built and validated on the administrator workstation; Linux x64 is built in Ubuntu
  24.04 under WSL2 on that same workstation. RC1 also runs Robotopia through pinned Proton under WSLg and records that
  the evidence is same-host and non-independent. `release-platform-bundle-v1` and `release-handoff-v1` bind both
  outputs and scrubbed QA evidence to one source SHA.
- Candidate metadata includes `release-bom.json`, `SHA256SUMS`, SPDX SBOMs,
  project/third-party notices, BepInEx provenance, two platform manifests, the
  aggregate handoff manifest and its detached pinned-certificate CMS
  signature, signed launcher-update metadata and sidecar, and checked-in
  release notes. The manual release catalog is future stable-only Pages output,
  not an RC1 asset.

## CI and privilege boundaries

GitHub-hosted workflows cover general CI, unsigned release dry-runs, Unity source/VPM validation, registry validation,
Pages, and protected release finalization. Pull-request validation is secretless. Repository code may build a Pages
artifact with `contents: read`; the separate deploy job has Pages/OIDC write permissions but performs no checkout or
arbitrary command. Production platform building, Unity activation, and Robotopia acceptance stay on
administrator-controlled machines. GitHub receives deterministic handoff manifests, verifies the staged bytes,
generates protected update metadata and a verifier attestation, then publishes after release-environment approval.

## Release-critical external boundaries

The codebase can enforce but cannot choose the project license, rights to Robotopia/TopiaForge assets and
compatibility work, privacy/backend policy, registry governance, or package trust root. It also cannot synthesize
future-platform signing credentials, GitHub rulesets/environments, WSL2, pinned Proton, legally authorized Robotopia
access, screen-reader review, or Robotopia profiler/gameplay evidence. RC1's
mandatory Windows signing and same-host Proton decisions are explicit,
fail-closed policy—not silently skipped gates.
