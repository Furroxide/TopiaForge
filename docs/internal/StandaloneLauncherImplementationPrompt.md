# TopiaForge Standalone Launcher Implementation Prompt

Use this prompt as the source of truth for implementing the next-generation TopiaForge modding platform. It is intentionally longer than a chat goal so the implementation agent has enough context, constraints, and done criteria.

## Short Chat Goal

```text
Implement the standalone TopiaForge mod launcher/loader described in docs/StandaloneLauncherImplementationPrompt.md. Treat that file as the full source of truth. Build in vertical slices, preserve existing runtime behavior, use Flutter with bloc/cubit for the app, keep licensing clean, and verify with .NET and Flutter tests before final summary.
```

## Full Prompt

You are implementing the next-generation TopiaForge modding platform.

### Goal

Pivot the current in-game TopiaForge Mod Manager into a standalone, Prism Launcher-quality desktop mod launcher/loader for Robotopia.

Keep BepInEx as the game-side runtime loader where appropriate, but make the primary user experience a polished standalone launcher app that can detect, install, manage, repair, and launch Robotopia with mods. The in-game overlay should become a fallback diagnostics/runtime-status panel only.

The result should feel like a mature modding ecosystem rather than a proof-of-concept overlay.

### Context

Current repo: the repository root.

Existing code includes:

- `src/TopiaForge.ModManager.Core`
- `src/TopiaForge.ModManager`
- `src/TopiaForge.Mods.Abstractions`
- `templates/mod/*` (scaffolding templates for `topiaforge new mod`)
- `tools/*.ps1`
- `docs/Modding.md`

Existing manager behavior to preserve:

- `.topiaforgemod` packages
- `topiaforge.mod.json` manifests
- dependency sorting
- enable/disable state
- package inbox install
- manager logs
- BepInEx runtime loading
- restart-required semantics for loaded C# assemblies

Prior analysis found that RoboPatch is useful community prior art, but it is not a full launcher/manager. RoboPatch has Robotopia-specific behavior worth matching or improving: AssetBundle helpers, prompt overrides, prompt conflict reporting, and simple drop-in mod support. Do not copy RoboPatch code unless license obligations are intentionally accepted and documented.

Target maturity should be inspired by Prism Launcher: profiles/instances, clean install and repair flow, modpack import/export, safe mode, diagnostics, update flow, and excellent mod browsing/install UX.

### Architecture Requirements

- Add a standalone Flutter desktop app, Windows-first.
- Use bloc/cubit architecture for app state.
- Suggested workspace layout:
  - `apps/topiaforge_launcher_flutter/`
  - `packages/launcher_domain/`
  - `packages/launcher_data/`
  - `packages/launcher_ui/`
  - existing `src/TopiaForge.*` C# runtime/loader projects
- Keep domain logic testable and UI-independent.
- Use a repository interface for package sources, profiles, installs, diagnostics, and launch orchestration.
- Use Serverpod only if a backend is actually needed. Default v1 to no backend: use a local/static mod index JSON model behind a repository interface so Serverpod can be added later without rewriting the app.
- Keep BepInEx and the TopiaForge runtime loader as the injected game-side component.
- Make the standalone launcher responsible for:
  - detecting Robotopia installs
  - installing and repairing BepInEx
  - installing and updating the TopiaForge loader
  - installing, updating, rolling back, and uninstalling mods
  - enabling and disabling mods
  - resolving dependencies and conflicts
  - managing profiles
  - launching Robotopia
  - collecting diagnostics

### Product Requirements

#### First-Run Flow

- Detect Robotopia at the known launcher path.
- Allow manual game-folder selection.
- Validate `Robotopia.exe`.
- Validate Unity Mono and BepInEx compatibility as far as practical.
- Offer one-click setup or repair for BepInEx and the TopiaForge loader.
- Explain that C# mods are trusted code and execute inside the game process.
- Avoid making the warning feel hostile; make it clear and actionable.

#### Profiles and Instances

- Support multiple profiles.
- Each profile should track:
  - enabled mods
  - selected mod versions
  - config metadata
  - launch settings
  - backup/save metadata where practical
- Include:
  - safe mode
  - disable all mods
  - duplicate profile
  - export profile
  - import profile
- Store profile state in a durable local data folder with a documented schema.

#### Package and Mod Management

- Preserve `.topiaforgemod` support.
- Extend the manifest schema for:
  - dependency version ranges
  - optional dependencies
  - conflicts and incompatibilities
  - supported game version ranges
  - supported loader version ranges
  - category and tags
  - icon and screenshots
  - homepage/source links
  - license metadata
  - package hashes
- Support install from:
  - local file
  - drag and drop
  - package inbox
  - local/static registry
- Show dependency and conflict plans before install.
- Never silently install conflicting mods.
- Support update checks, changelogs, rollback to a previous installed version, and uninstall cleanup.
- Support modpack/profile export and import.

#### Clean-room compatibility boundaries

- Clean-room reimplement equivalent SDK conveniences:
  - AssetBundle loading
  - `SpawnAsset`
  - prompt override
  - prompt conflict diagnostics
  - mod file helpers
- Do not copy RoboPatch code.

#### UX and UI

- Build a real desktop utility app, not a landing page.
- Use a quiet, polished, work-focused layout.
- Recommended app structure:
  - left navigation
  - profile selector
  - prominent launch button
  - status bar
  - dense mod list
  - detail pane
- Required screens:
  - Library/Launch
  - Mods
  - Browse
  - Profiles
  - Diagnostics
  - Settings
- Include:
  - search/filter/sort
  - empty states
  - loading states
  - error states
  - update badges
  - dependency warnings
  - repair notices
  - logs viewer
  - destructive-action confirmations
  - keyboard accessibility
  - visible focus states
  - responsive desktop sizing
  - no text overflow
- Use familiar icons for actions where practical.
- Avoid oversized marketing sections, decorative cards, and landing-page composition.
- The first screen should be useful to a player immediately.

#### Diagnostics

- Collect:
  - launcher logs
  - BepInEx logs
  - manager logs
  - mod load order
  - dependency graph
  - game path
  - BepInEx status
  - loader status
  - last launch result
- Add a "Create diagnostic bundle" action that produces a zip.
- Redact tokens and user-private paths where feasible.
- Add repair actions:
  - reinstall loader
  - reinstall or repair BepInEx
  - clean stale staging directories
  - disable all mods
  - open relevant folders

#### Security and Integrity

- Treat C# mods as trusted code.
- Validate:
  - zip traversal
  - malformed manifests
  - duplicate IDs
  - invalid versions
  - missing DLLs
  - dependency failures
  - conflicts
  - oversized packages
- Add SHA-256 hashing for packages and registry entries.
- Design for signatures later, but do not fake signature enforcement.

### Licensing and Attribution

- Inspect RoboPatch `LICENSE` before copying or porting any code.
- RoboPatch currently appears GPL-3.0 licensed; verify upstream before using code.
- Prefer clean-room reimplementation from behavior and documentation instead of direct copying.
- If any RoboPatch code is copied or ported:
  - ensure the resulting licensing is compatible
  - add `THIRD_PARTY_NOTICES.md`
  - preserve copyright notices
  - include the relevant license text
  - mark modified files
  - document provenance
- Do not copy Prism Launcher code. Use it only as UX/product inspiration unless license compatibility is intentionally accepted and documented.
- Preserve attribution and licenses for BepInEx and any bundled third-party runtime assets.

### Implementation Process

First inspect the repo and current tests. Then add or update `AGENTS.md` with:

- project conventions
- verification commands
- licensing rules
- UI quality bar
- expected Flutter bloc architecture
- expected C# runtime boundaries

Implement in runnable vertical slices:

1. Workspace/app scaffold.
2. Domain models and persistence.
3. Existing package manager integration.
4. Flutter shell and core screens.
5. Install, repair, and launch flows.
6. RoboPatch compatibility/import.
7. Diagnostics and tests.
8. Docs and distributable packaging.

Keep each slice buildable. Do not leave the repo in a half-wired state if it can be avoided.

### Verification

Run existing .NET build and tests.

Add focused tests for:

- manifest parsing
- dependency resolution
- profile state
- registry parsing
- install/update/uninstall
- zip path traversal

Run Flutter analysis and tests.

Manually launch the Flutter desktop app and verify:

- first-run detection
- manual path selection
- loader install/repair
- profile creation
- local package install
- enable/disable
- dependency/conflict display
- launch
- diagnostics bundle creation
- repair actions

### Done Criteria

The task is done when a user can:

- open the standalone launcher
- detect or select Robotopia
- install or repair the loader
- create and select a profile
- install `.topiaforgemod` packages
- view dependency and conflict status
- enable and disable mods
- launch Robotopia
- generate diagnostics

Also required:

- existing runtime loader functionality still builds
- relevant tests pass
- licensing attribution is correct
- UI is materially better than the current in-game overlay
- architecture is on a credible path toward Prism Launcher-level maturity
