# Contributing to TopiaForge

Thank you for helping improve the TopiaForge modding ecosystem. Read `AGENTS.md` before changing code; it is the
authoritative repository guide for architecture, generated artifacts, UI quality, and required verification.
The protected branch flow, merge-method rules, release gates, and break-glass process are documented in
[`docs/RepositoryGovernance.md`](docs/RepositoryGovernance.md).

## Before starting

1. Run `pwsh ./tools/bootstrap-dev.ps1 -Verify` and follow `docs/ContributorSetup.md`.
2. Search existing issues and keep each pull request focused on one coherent change.
3. Discuss public API, package/schema, save-format, registry-policy, or compatibility changes before implementation.
4. Do not copy RoboPatch or Prism Launcher code. Record the source, license, version, and local modifications for
   every third-party asset or code contribution.

The repository does not yet declare project-wide contribution licensing terms. Maintainers must resolve that policy
before accepting substantive external contributions; prospective contributors should confirm the intended terms with
the repository owner first.

## Engineering boundaries

- Keep launcher domain logic independent of Flutter and operating-system I/O.
- Put filesystem, network, archive, and process work behind data-layer services.
- Use `Bloc<LauncherEvent, LauncherState>` for launcher state; do not introduce Cubits.
- Keep `TopiaForge.ModManager.Core` Unity-free and Unity/BepInEx code in the runtime project.
- Build all in-game UI through TopiaForgeUi and keep non-generated Dart files at 500 lines or fewer.
- Treat manifests, archives, registries, process arguments, and downloaded content as untrusted input.

## Verification

Run the checks relevant to your change, including every command required by `AGENTS.md`. The full pre-PR baseline is:

```powershell
dotnet build TopiaForge.slnx -c Release
dotnet run --project tests/TopiaForge.ModManager.Tests/TopiaForge.ModManager.Tests.csproj -c Release

Push-Location packages/launcher_domain; dart format --output=none --set-exit-if-changed lib test; dart analyze; dart test; Pop-Location
Push-Location packages/launcher_data; dart format --output=none --set-exit-if-changed lib test; dart analyze; dart test; Pop-Location
Push-Location apps/topiaforge_cli; dart format --output=none --set-exit-if-changed bin lib test; dart analyze; dart test; Pop-Location
Push-Location packages/launcher_ui; dart format --output=none --set-exit-if-changed lib test; flutter analyze; flutter test; Pop-Location
Push-Location apps/topiaforge_launcher_flutter; dart format --output=none --set-exit-if-changed lib test; flutter analyze; flutter test; Pop-Location
```

Unity authoring and TopiaForgeUi changes must use exactly Unity `6000.0.23f1`; include the batch-mode command and resulting
manifest/hash in the pull request. Never substitute another editor version.

## Pull requests

Explain the user-visible outcome, compatibility impact, security implications, and verification performed. Include a
regression test for meaningful bugs. Do not commit generated build directories, credentials, local game files, or
managed game assemblies. Keep release, signing, Unity-license, registry-publishing, and Pages credentials out of PR
workflows and logs.

Open normal and community pull requests against `dev` and use a Conventional Commit title. Owner-authored topics are
squash merged. External contributors must sign every commit and use a merge commit; unsigned patches are restaged by
the maintainer in a new owner-authored PR. Dependabot and `sync/main-v*` PRs also use merge commits. Contributors must
not target `main` or `release/*` directly.
