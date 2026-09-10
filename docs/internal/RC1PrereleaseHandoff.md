# RC1 prerelease merge and publication handoff

Updated 2026-09-10. Target: `release/0.1.0-rc.1` into `main`, then
`v0.1.0-rc.1` as an early prerelease of 0.1.0. **Not qualified for publication.**
This handoff records preparation; it supplies no reviewer approval or candidate
acceptance. The [release checklist](../ReleaseChecklist.md),
[administrator runbook](../AdminRelease.md), and
[tracked readiness register](../../release/release-readiness.json) remain authoritative.

## Scope and disclosures

The existing catalog declares `prerelease: true`. RC1 distributes one Windows
x64 archive for Robotopia build 2409 and thirteen first-party mod packages. The
archive embeds two VPM packages. Linux and macOS are outside this release; no
future platform release date or version is promised.

Windows executables are intentionally unsigned. This does not waive checksums,
Ed25519 update signatures, exact-byte verification, or protected publication.
The [release notes](../../release/notes/v0.1.0-rc.1.md) describe the public
experimental scope and limitations. A disclaimer does not establish permission
to redistribute assets or evidence of a successful test.

## Before the final main merge

- Integrate release stabilization through ordinary same-repository PRs into
  `release/0.1.0-rc.1`. Preserve unrelated local work and do not infer that an
  uncommitted implementation belongs in the frozen candidate.
- Preserve the dependency repairs integrated through
  [PR #129](https://github.com/Furroxide/TopiaForge/pull/129): Astro 7.2.8,
  Sharp 0.35.4, smol-toml 1.8.0 and SVGO 4.1.0. Require fresh passing dependency
  review on the final release head, alongside all other required checks and CodeQL.
- Obtain actual attributable approvals for `P0-IP-01`, `P0-OSS-01`,
  `P0-PRIV-01`, and `P0-CRED-01`, and integrate their valid safe references.
  Their tracked records are blocked with no evidence IDs. Only `P0-GAME-01`
  may await the final candidate. Credential closure was previously deferred;
  release intent does not supply closure evidence.
- Preserve all advisory rows and record actual dated owner dispositions where
  evidence remains incomplete. Keep private review records, raw logs and
  credentials outside Git and public release assets.
- Configure the dedicated protected `release` environment secret
  `TOPIAFORGE_GOVERNANCE_AUDIT_TOKEN` through secure entry. It was absent in the
  2026-09-10 name-only check. Follow the repository-scoped read-only permissions
  in [RepositoryGovernance](../RepositoryGovernance.md#trusted-release-and-deployment-environments).
  The update-signing secret exists; existence does not prove recovery or validity.
- Complete reviewed QA admission, neutral build preparation, and signing-key
  recovery prerequisites under their actual authorizations. Existing source
  tests, development QA provisioning and synthetic package runs cannot replace
  those records or final candidate acceptance.
- Refresh [PR #119](https://github.com/Furroxide/TopiaForge/pull/119) with the
  final release head and results. Disable draft only when source preparation
  is complete. Leave auto-merge off; the user performs the final merge.

At the initial 2026-09-10 inspection of `6d4082d`, dependency review was
PR #119's sole failing required check. PR #129 repaired that failure. At
`f0ef217`, all required checks and CodeQL passed; PR #119 remained a draft.
No RC1 tag or GitHub release existed. Later release heads need fresh checks.

## Integrated stabilization evidence

PR #129 was squash-merged normally at
`f0ef217485bb4519e202387c2f68757316d746f2` after all required checks and CodeQL
passed on reviewed head `7859046ca6cef60d49955b06bee01363fd406e7e`.
[Full CI](https://github.com/Furroxide/TopiaForge/actions/runs/34418453127)
passed all 16 jobs, including seven templates and the complete guide, C# API,
Dart API and search build. Local website validation passed 50 tests and reported
zero npm audit vulnerabilities. On integrated release head `f0ef217`,
[main-target CI](https://github.com/Furroxide/TopiaForge/actions/runs/34419132880)
and [release-push CI](https://github.com/Furroxide/TopiaForge/actions/runs/34419131141)
each passed all 16 jobs; both CodeQL runs passed. The
[packaging dry run](https://github.com/Furroxide/TopiaForge/actions/runs/34419133863)
passed all 11 jobs. Later source changes require fresh checks. None of these
source or synthetic package results qualifies the production candidate.

The subsequent cleanup repair propagates production creator-source disposal
failures through stop, removal, undo, graph actions and session teardown. It
continues unrelated owned cleanup, preserves startup and rollback errors, and
prevents retired callbacks from restoring undo history. Production-backed
regressions cover eleven cleanup routes plus session retirement and startup
rollback. Generated runtime acceptance fixtures use their explicit local SDK
feed without inheriting unrelated host feeds; failures retain subprocess output
and the existing deadlines. Local full Release build, manager and runtime
harnesses pass. The PR's fresh hosted checks remain required before integration.

The original development checkout retains separate unfinished Sandbox automation
and provisioning work. Its source tests and earlier pinned-Editor results are
not candidate acceptance. Full native automation, a clean runtime provisioning
retry, reviewed QA admission and the final candidate authoring/game matrix
remain incomplete. The user has not answered the full-versus-reduced experimental
scope question; no requirement or deferred decision has been changed.

## After the user merges

1. Verify the final `main` commit has two parents, its second parent is the
   checked release head, and its tree matches that head. Freeze that actual SHA
   in a clean checkout equal to `origin/main`.
2. Use the same explicit external state, verified source-game and reviewed QA
   record paths throughout `release-admin.ps1 preflight` and `build`. Preflight
   must report `eligible-for-private-build`; it does not grant SHIP approval.
3. Build and test the exact candidate bytes. Complete the required SDK cases,
   Unity authoring cycles, gamemode cases and isolated native game acceptance.
   Preserve failures and incomplete observations; do not substitute hosted
   dry-run artifacts or development fixtures for the candidate.
4. Obtain reviewed detached candidate readiness and acceptance records and run
   `release-admin.ps1 qualify`. It must record `accepted`. Freeze the payloads;
   changes require a new build and acceptance.
5. Obtain the recorded final SHIP decision. Run `stage` to create the signed
   annotated tag and matching draft with the 18 approved human-owned assets.
   Tagging occurs here, after qualification.
6. Run `dispatch`, then obtain the protected `release` environment approval.
   The finalizer verifies and publishes the exact prerelease, adding five
   metadata assets for 23 public assets. Keep the prerelease flag true and
   exclude it from stable discovery feeds.
7. Verify the immutable release and assets, published version, checksums,
   Ed25519 metadata and verifier attestation. Retain the publication receipt.

The final-main candidate does not exist before the merge. Successful source
promotion therefore cannot promise immediate publication: candidate construction,
acceptance, qualification and protected approval still follow in that order.
