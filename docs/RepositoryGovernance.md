# Repository governance

This document is the desired GitHub governance contract for TopiaForge. It complements `AGENTS.md`,
`CONTRIBUTING.md`, `SECURITY.md`, and `docs/ReleaseChecklist.md`; it does not replace their engineering, disclosure,
or release instructions.

TopiaForge is currently a public, personal repository with one trusted maintainer. The rules deliberately require
pull requests and automated evidence while requiring zero approvals. Requiring the sole maintainer to approve their
own work would make administrator bypass the normal path and would weaken the audit trail. CODEOWNERS is advisory
until a second trusted maintainer is available.

## Protected branch flow

`main` is the stable/default branch and `dev` is the integration branch. The supported paths are:

1. Create topic branches from current `dev`. Community and fork pull requests target `dev` only.
2. Squash normal topic pull requests into `dev` after all required checks pass.
3. For a normal release, create `release/<semver>` from `dev`. For a security patch, create it from stable `main`.
4. Squash focused stabilization pull requests into the release branch.
5. Merge `release/<semver>` into `main` with a merge commit after the exact release head passes every required check.
6. Create `sync/main-v<semver>` from current `dev`, merge the new `main` into it with a signed merge commit, and merge
   that synchronization pull request into `dev` with a merge commit. The PR policy gate verifies that current `main`
   is an ancestor of the sync head.
7. Run the trusted exact-`main` release gates. Only then create a signed, annotated `v<semver>` tag on that SHA.

Do not merge `main` or a release branch directly back into `dev`. The named sync branch preserves ancestry without
forcing `main` to absorb unrelated development work merely to satisfy strict status-check freshness.

### Merge methods and signatures

- Owner-authored topic and stabilization pull requests use **squash merge**. The Conventional Commit pull-request
  title and body become the commit subject and body.
- `release/*` to `main` and `sync/main-v*` to `dev` use **merge commits** to preserve ancestry.
- Dependabot version-update pull requests target `dev` and use **merge commits**. The maintainer is not the bot-authored
  PR author, and GitHub cannot create an accepted squash commit for that case under required signed commits.
- Rebase merging is disabled. Auto-merge and automatic branch deletion remain disabled. "Update branch" is enabled.
- External contributors must sign every commit. Their PRs use a GitHub **merge commit** because a maintainer cannot
  squash another author's PR into a branch that requires signatures. If commits are unsigned, the maintainer must
  restage the reviewed patch on an owner-authored branch and open a new PR; protection is not bypassed.

GitHub cannot condition merge methods on the source branch. Reviewers must select the method above; ancestry checks
and release verification provide additional evidence for the exceptional merge-commit paths.

## Required evidence

All required status checks are strict, must come from the GitHub Actions app, and use stable aggregate job names. Leaf
matrix jobs are never configured directly as required checks.

| Target | Required checks |
| --- | --- |
| `dev` | `Required / PR policy`, `Required / CI validation`, `Required / Unity source validation`, `Required / Registry validation`, `Required / Dependency review` |
| `release/*` | The same five common checks. Each release-head update also produces `Required / Release packages` for later promotion. |
| `main` | The five common checks plus `Required / Release packages` from the exact release-branch head. |

Each aggregate job uses `if: always()` and fails unless all jobs it represents succeed. Dependency review rejects a
new dependency with a moderate-or-higher known vulnerability in runtime, development, or unknown scope. License
blocking remains off until project-wide inbound and outbound licensing is resolved.

CodeQL is an independent ruleset gate at high-or-critical severity. Default setup covers Actions, C/C++, C#,
JavaScript/TypeScript, and Swift with the default query suite on a weekly schedule. Dart remains covered by analyzer
and test CI.

## Ruleset contract

Separate active branch rulesets protect `main`, `dev`, and `release/*`. They require pull requests, signed commits,
resolved review conversations, strict status checks, and CodeQL; they reject force pushes and have no bypass actors.
`main` and `dev` also reject deletion. While governance is single-maintainer, required approvals and CODEOWNER
approvals are both zero/disabled.

Reference lifecycle is intentionally separate from branch updates:

- `release/*` creation and deletion are restricted. The repository administrator role may bypass only this lifecycle
  rule so the owner can create and retire release branches; it does not bypass protected updates.
- `v*` creation is restricted and only the owner/admin lifecycle bypass may create a release tag.
- `v*` updates and deletions are prohibited by a separate no-bypass ruleset. A failed version is never moved or reused.

Applicable GitHub rulesets compose, so classic branch protection must not duplicate these rules. The checked-in
desired-state manifest is `.github/repository-governance.json`, and the read-only audit command is:

```bash
python3 .github/scripts/audit_repository_governance.py check
```

Capture a scrubbed, read-only live snapshot before and after a governance change:

```bash
python3 .github/scripts/audit_repository_governance.py snapshot > governance-snapshot.json
python3 .github/scripts/audit_repository_governance.py check --snapshot governance-snapshot.json
```

The snapshot contains settings and protection metadata, never credential values. Keep incident snapshots with the
corresponding private maintainer record if they contain reviewer or account identifiers.

## Pull-request policy

Every protected-branch PR title follows Conventional Commits, for example `fix(runtime): reject an unsafe path`.
The stable PR-policy check enforces these routing rules:

- Fork/community PRs may target only `dev`.
- Dependabot PRs may target only `dev`.
- `release/*` accepts same-repository stabilization branches only.
- `main` accepts only the same repository's exact `release/<productVersion>` branch, where `productVersion` is read
  from that head's `release/release-policy.json`.
- Direct `main` or `release/*` backflow to `dev` is rejected; `sync/main-v<semver>` is required and must contain the
  current `main` tip as an ancestor.

Only explicitly trusted collaborators may submit formal Approve or Request Changes reviews. Community review remains
welcome through comments and suggestions; formal reviews become enforcement evidence only when collaboration trust
has been granted.

## Trusted release and deployment environments

Environments must exist before any workflow references them. Otherwise GitHub can auto-create an unprotected
environment. Administrator bypass is disabled on every privileged environment. Self-review is temporarily allowed
because there is one maintainer.

| Environment | Allowed ref | Purpose |
| --- | --- | --- |
| `release` | `v*` tags | Signing, notarization, managed-reference, attestation, and release preparation credentials |
| `unity-validation` | protected `main` | Unity licensing and exact-SHA trusted build validation |
| `game-acceptance` | protected `main` | Dedicated Robotopia host variables and live-game acceptance |
| `github-pages` | published `v*` tags | Deployment of release-derived Pages content |

Every job that can read signing, notarization, managed-reference, publication, Unity, or live-host credentials declares
its protected environment directly. An ungated caller never forwards those secrets into a reusable workflow. PR
workflows are read-only and secretless.

Live-game acceptance is manual, checks out an explicit SHA from `main`, and reports `Required / Game SDK acceptance`.
It is not an ordinary PR gate. Release readiness remains blocked until dedicated self-hosted runners are registered;
those hosts are reset or reimaged between trusted runs.

## Release sequence

1. Merge the fully green release PR into `main` with a merge commit.
2. Run trusted Unity validation and full Windows/Linux-Proton game acceptance against that exact `main` SHA.
3. Create a verified, signed, annotated `v<semver>` tag on that SHA. Never use a lightweight tag.
4. Dispatch production release preparation from the protected tag.
5. Before an environment approval unlocks credentials, verify the associated release PR and release-head check, both
   exact-main-SHA gate runs, tag object/type/signature/target, release policy, catalog, and artifact inventory.
6. Build signed assets, generate project SBOM/BOM/checksums, create GitHub artifact attestations, and create or resume
   only the matching draft release.
7. Inspect and publish manually. Immutable releases then lock the tag and assets; verify with `gh release verify` and
   asset verification.

The unsigned release dry run is secretless and executes on every `release/*` update. Production signing is a separate,
environment-gated entrypoint. Pages builds from published immutable releases only; its build phase is read-only, and
the Pages/OIDC deployment job neither checks out nor executes repository code.

## Repository security settings

The desired live settings are:

- Default `GITHUB_TOKEN` permission is read-only; Actions cannot approve pull requests.
- All Actions are pinned to a full commit SHA. GitHub-owned Actions are allowed, plus only
  `dart-lang/setup-dart`, `subosito/flutter-action`, and `game-ci/unity-builder`.
- Workflow approval is required for every external contributor.
- Dependency graph and Dependabot vulnerability alerts are enabled. Automated Dependabot security-update PRs remain
  disabled because they target default `main`; maintainers turn alerts into controlled patch-release branches.
- Routine GitHub Actions, NuGet, npm, and Pub version updates target `dev`. The pending `/website` Dependabot entry
  becomes operational once that lockfile reaches the default branch.
- Private vulnerability reporting, secret scanning, and push protection are enabled. GitHub does not offer
  non-provider secret patterns or validity checks to this public personal repository; enable both after a transfer to
  an eligible organization plan.
- Immutable releases are enabled before publication. The repository push limit is five refs per push, preventing
  accidental mirror pushes. Wiki is disabled; Issues remain enabled; DCO/web signoff remains disabled until inbound
  licensing is resolved.

The five-ref push limit and owner-account security posture require manual verification because GitHub does not expose
all of them through the repository audit API. The sole owner must maintain a passkey or hardware security key, TOTP
recovery, offline recovery codes, and regularly reviewed sessions, PATs, SSH keys, and recovery channels.

## Break glass

Protection bypass is exceptional and is never a direct push:

1. Open an incident issue or private security incident record describing the blocker, affected refs, and expected
   duration.
2. Capture the pre-change governance snapshot and exact ruleset versions.
3. Add the narrowest temporary **pull-request-only** bypass to the affected ruleset. Do not add an `always` bypass.
4. Merge only the recorded PR after all checks that can run have passed and record why any unavailable gate was safe
   to defer.
5. Remove the bypass immediately, rerun the audit, and retain the ruleset-suite/event evidence with the incident.
6. Follow up with a corrective PR so the same bypass is not needed again.

A version tag can be corrected only through this documented process and a temporary ruleset change. Never move or
reuse the failed version; mint the next version after restoring protection.

## Governance graduation

When two trusted maintainers are available, update all protected branch rulesets to require one independent approval,
a CODEOWNER review, stale-review dismissal, and approval of the last push. Disable environment self-review. Revisit an
organization transfer, merge queue, organization-required workflows, team-based CODEOWNERS, and stronger separation
of release duties when contributor concurrency justifies them.

GitHub references: [rulesets](https://docs.github.com/en/repositories/configuring-branches-and-merges-in-your-repository/managing-rulesets/about-rulesets),
[available rules](https://docs.github.com/en/repositories/configuring-branches-and-merges-in-your-repository/managing-rulesets/available-rules-for-rulesets),
[secure Actions use](https://docs.github.com/en/actions/reference/security/secure-use),
[deployment environments](https://docs.github.com/en/actions/reference/workflows-and-actions/deployments-and-environments),
[Dependabot configuration](https://docs.github.com/en/code-security/reference/supply-chain-security/dependabot-options-reference),
and [immutable releases](https://docs.github.com/en/code-security/concepts/supply-chain-security/immutable-releases).
