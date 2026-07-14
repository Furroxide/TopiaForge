# Robotopia ecosystem inventory

This is the release-facing component and contract map for the complete repository. It records ownership boundaries,
not merely build projects. Update it whenever a public contract, generator, or release payload changes.

## Runtime and C# SDK

| Component | Responsibility | Direct boundaries / outputs |
| --- | --- | --- |
| `Robotopia.ModManager.Core` | Manifest, version, dependency, path, state, package, and profile-domain logic | Unity-free `netstandard2.1`; consumed by the BepInEx runtime and C# tests |
| `Robotopia.ModManager` | BepInEx plugin, startup/shutdown, runtime install state, mod loading/isolation, scenes, logs, package inbox, manager overlay | May reference Unity/BepInEx; ships as the game-side loader |
| `Robotopia.Mods.Abstractions` | Public mod SDK contracts and services | Additive public API, `netstandard2.1`, versioned independently from the loader |
| `Robotopia.Mods.UnityUi` | QwUi host, themes, allocator, widgets, motion, accessibility, and embedded brand bundle | Unity-only SDK extension; consumers must not build raw uGUI |
| `Robotopia.GameCompat.Surface` | Serializable game-code surface and comparison rules | Unity-free contract shared by extractor/tests |
| `Robotopia.GameCompat.Extractor` | Metadata-only installed-game inspection | Self-contained developer/release executable; never loads game code for execution |
| `Robotopia.ModManager.Tests` | Cross-component C# harness | Exercises Core, SDK, GameCompat, runtime source conventions, and pure mod seams |

The primary solution also builds all thirteen first-party mods: Assets, Chronos, GravityGun, NoFeedbackUrl, PerfFixes,
Performance, Prompts, RobotKit, Sandbox, UgcLiveSync, UiGallery, Worlds, and Zombies. Their public dependency graph is
expressed only through `robotopia.mod.json`; project references to the SDK/QwUi are compile-time implementation details.
UiGallery is a validated developer catalog and is excluded from the normal player payload.

## Launcher and developer tooling

| Component | Responsibility | Allowed dependencies |
| --- | --- | --- |
| `launcher_domain` | Immutable models, SemVer/ranges, manifests, profiles, dependency/install planning, registry contracts | Dart only; no Flutter, filesystem, network, archive, or process APIs |
| `launcher_data` | Local repositories and services for storage, downloads, archives, processes, runtime repair, diagnostics, UGC, and Unity/VPM tooling | Depends on `launcher_domain`; returns domain-ready typed data |
| `launcher_ui` | Shared Flutter theme, motion, and presentation widgets | Flutter only; no application state or data access |
| `robotopia_launcher_flutter` | Desktop application and `LauncherBloc` event/state coordination | Depends on all launcher packages; widgets dispatch events and never perform data I/O |
| `robotopia` CLI | Mod/template/registry/VPM/world/UI/release commands and deterministic packaging | Reuses domain/data services; is not a second implementation of archive or process policy |
| UGC Automerge sidecar | Optional Node 20+ publisher and session lease | Lockfile-backed, separate process; never required for ordinary mod/player flows |

## Package and serialization contracts

| Contract | Version / compatibility rule | Producers and consumers |
| --- | --- | --- |
| `.robotopiamod` ZIP + `robotopia.mod.json` | Manifest schema 2; additive optional fields and ignored unknown fields; published bytes immutable | CLI/scaffolds/first-party builds produce; launcher and runtime validate/consume |
| SemVer and version ranges | SemVer 2.0 precedence; exact, wildcard, and comparator-set ranges | C# Core and Dart domain must pass shared parity fixtures |
| Game build version | Numeric build `N` maps to `0.0.N`; initial release is exactly `0.0.2227` | Extractor/runtime detect; launcher plans; manifests constrain |
| Manager/profile/session state | Bounded, atomic, backward-readable; profile pins select exact installed package versions | Launcher data writes; runtime reads process-scoped session state |
| Registry entry/index | Format 1, additive readers, append-only published history, HTTPS + SHA-256 | CLI builds/validates; launcher data consumes as untrusted input |
| UGC config/status/command/session | Explicit schema versions, bounded JSON, atomic writers, unknown fields tolerated where documented | Launcher/CLI/sidecar/`Robotopia.UgcLiveSync` |
| World and QwUi bundle manifests | Exact Unity `6000.0.23f1`, target, inputs, and SHA-256 provenance | Unity batch builders produce; CLI/package/runtime validate |
| Release policy/BOM/catalog | Product/component versions and expected artifacts are checked against source metadata; catalog is manual-only | CLI/workflows produce; release gate and human reviewers consume |

## Templates and authoring surfaces

- Seven C# mod templates: minimal, gameplay, gamemode, service, UI, asset, and world.
- Unity world project template with the world companion and embedded VPM resolver.
- Standalone Unity package template and UGC companion package.
- QwUi bundle source project under `tools/unity-ui-bundle`.
- Three first-party VPM packages/listings generated and validated by the CLI.

Templates are release inputs, not documentation snippets: every template is scaffolded, built, packed twice, and
validated from its resulting archive. Default scaffolds are deliberately non-publishable until author and license
identity are supplied.

## Compatibility, registry, and repository support data

- `baselines/gamecode.surface.baseline.json` is the reviewed build-2227 compatibility surface. The extractor may
  propose an update, but release validation rejects an unexplained or different-build baseline.
- `bindings/*.gamebindings.json` are the ten first-party runtime binding declarations consumed by the compatibility
  audit. They are contract inputs, not generated success evidence; dynamic/value bindings still require in-game QA.
- `registry/` intentionally contains no official community entries for v1. Its README and the format-1 CLI commands
  support local and self-hosted sources while submissions are closed.
- `third_party/BepInEx/` contains the pinned 5.4.23.5 Windows/macOS archives, extracted payloads, license texts, and
  immutable provenance consumed by runtime repair and release packaging.
- `.githooks/` contains repository-owned commit/push/LFS validation hooks; `.vscode/` selects the project FVM SDK and
  recommends editor integrations. Neither is a runtime dependency.
- `data/` is reserved and currently has no tracked release input. `build/` and `dist/` are generated/ignored work
  areas and must never be treated as authoritative source or silently reused by a clean release build.

## Generated and release artifacts

- Ignored managed game references and `Directory.Build.local.props` are generated from the exact hashed game-build
  pin; proprietary assemblies are never committed or released.
- Flutter platform registrants, lock files, Unity `.meta` files, bundle manifests, prefab assets, and VPM listings are
  generated artifacts whose source/provenance must agree with their generators.
- A release candidate consists of one canonical deterministic ecosystem payload plus Windows x64, Linux x64, and
  macOS universal platform archives. Nested mod/VPM hashes must be identical between platforms.
- Candidate metadata includes `release-bom.json`, `SHA256SUMS`, SPDX SBOMs, project/third-party notices, BepInEx
  provenance, the manual release catalog, and checked-in release notes.

## CI and privilege boundaries

Ten workflows cover general CI, Flutter/native builds, release dry-runs/build/publication, Unity artifacts, registry
validation, and Pages deployment. Pull-request validation is secretless. Repository code may build a Pages artifact
with `contents: read`; the separate deploy job has Pages/OIDC write permissions but performs no checkout or arbitrary
command. Signing, notarization, Unity activation, tag/release mutation, attestations, and Pages deployment are trusted
candidate operations protected by environments and stable aggregate checks.

## Release-critical external boundaries

The codebase can enforce but cannot choose the project license, rights to Robotopia/QuantumWorks assets and
compatibility work, privacy/backend policy, registry governance, or package trust root. It also cannot synthesize
Apple/Windows signing credentials, GitHub rulesets/environments, clean native hosts, legally authorized game access,
screen-reader review, or in-game profiler/gameplay evidence. Each remains an explicit blocker rather than a skipped
gate.
