# Admin-orchestrated release

Production archives are built only on maintainer-controlled machines. GitHub
Actions verifies the exact staged bytes, attests what it verified, creates the
signed update metadata, and publishes after approval of the protected
`release` environment. It does not rebuild production packages.

The entry point is:

```powershell
./tools/release-admin.ps1 preflight
./tools/release-admin.ps1 build `
  -WindowsCreatorEvidence C:\release-qa\windows-creator-evidence.json `
  -WindowsCreatorEvidenceBundle C:\release-qa\windows-creator-evidence.zip
./tools/release-admin.ps1 stage
./tools/release-admin.ps1 dispatch
```

`resume` continues from the durable local state and `all` runs every remaining
phase. Add `-Rehearsal` to `all` for a verified, non-publishing two-platform
rehearsal. Local state and raw evidence live under `.release-local/`, which is
ignored by Git.

Each canonical ecosystem pass runs in its own detached clean worktree. The
SHA-256 of the normalized, sorted tree manifest is the ecosystem identity; the
single tar sent to WSL has a separate transport digest. Every
platform validation summary binds both values.

The machine-readable ship decision is
`release/release-readiness.json`. It is read from the exact target commit,
validated against the schema from that same commit, and bound into the BOM by
digest and gate summary. It carries one entry for each of the twelve
release-fatal gates in [`LaunchBlockers.md`](LaunchBlockers.md), so no gate that
can stop a release is tracked only in prose. Every P0 gate must be `approved`.
Each P1 gate must be either `approved` or `accepted-risk` with an allowed RC1
scope and evidence identifier. A blocked, missing, malformed, working-tree-only, or wrong-version
decision stops preflight, staging, and the protected finalizer. The catalog
must remain `blocked` until the reviewed decision is committed. The CLI treats
the `evidenceIds` as manually reviewed attestation references: it validates
their exact syntax, uniqueness, gate binding, and exact-commit digest, but does
not resolve an external evidence registry or prove reviewer identity. Evidence
existence and reviewer authorization are therefore part of the protected
`release` environment's final human approval.

## Admin Windows machine

The checkout must be a clean `main` exactly equal to `origin/main`. Configure:

- GitHub CLI authentication for a repository administrator.
- live GitHub governance matching the checked-in release controls: immutable
  releases enabled, the non-bypassable `release` environment reviewed only by
  `furroxide` for `v*` tags, and the active release-branch/version-tag
  lifecycle and immutability rulesets;
- a Git tag-signing key through `user.signingKey`, with its matching public key
  registered as an SSH signing key (or GPG key, as applicable) for the
  authenticated GitHub administrator;
- .NET `10.0.301`, Flutter `3.44.6`, Dart `3.12.2`, Node `24.18.0`,
  Unity `6000.0.23f1`, MSVC `14.51.36231`, Windows SDK
  `10.0.26100.0`, Python 3.11 or newer, Git LFS, 7-Zip, tar, WSL, and
  GitHub CLI;
- an activated local Unity license and the Robotopia build-2309 installation.

On Windows systems where `python` resolves to the nonfunctional Microsoft
Store alias, set `TOPIAFORGE_PYTHON` (or pass `-PythonPath`) to an absolute,
working Python 3.11-or-newer executable. Preflight executes a version probe; a
path existing on disk is not sufficient.

RC1 and every later production release require Authenticode. Before freezing
the release commit, pin the reviewed leaf certificate SHA-256 in
`signingIdentities.windowsCertificateSha256` and supply
`WINDOWS_CERTIFICATE_PFX`, `WINDOWS_CERTIFICATE_PASSWORD`, and an HTTPS
`WINDOWS_TIMESTAMP_URL`. The pinned value is the leaf certificate fingerprint,
not the PFX digest. Preflight rejects missing, zero, expired, non-code-signing,
or mismatched credentials. The launcher, CLI, and GameCompat extractor must
all carry valid RFC 3161-timestamped signatures from that exact certificate.
The detached handoff CMS also carries exactly one RFC 3161 timestamp token.
Verification binds its message imprint to the CMS signer, requires trusted
code-signing and TSA chains with the appropriate EKUs, and evaluates both
leaf certificates at the timestamp instant. A `VerifyOnly` resumption reads
only the frozen handoff, P7S, policy pin, and platform trust store; it does not
require the PFX, its password, or the timestamp endpoint.

The protected `release` environment has two narrowly separated secrets:

- `TOPIAFORGE_UPDATE_ED25519_PRIVATE_KEY_B64` is the release update-signing
  seed. After testing recovery, remove every plaintext local duplicate and
  confirm the protected secret independently before staging.
- `TOPIAFORGE_GOVERNANCE_AUDIT_TOKEN` is a dedicated fine-grained PAT used
  only by the protected live-governance verification steps. Scope it to this
  repository with `Administration: read` and `Actions: read`; fine-grained
  tokens receive the required Metadata read permission implicitly. These cover
  the environments, immutable-release, and security-feature endpoints checked
  by `tools/verify-release-governance.sh` and
  `.github/scripts/audit_repository_governance.py`. It must have no write
  permission of any kind. Do not store a short-lived GitHub App installation
  token as this long-lived environment secret; an App-based design must instead
  store the App identity/key and mint a fresh installation token in-workflow.

The workflow continues to use its short-lived `GITHUB_TOKEN` for the
candidate-verification, attestation, and publication operations that actually
need workflow authority. Do not substitute the maintainer's broad interactive
GitHub CLI token for the audit secret.

The preflight opens the exact Unity project in batch mode to prove that the
local activation is usable. It never needs Unity email/password credentials.

Firmware virtualization must be enabled manually. Then install WSL2 and the
named distribution from an elevated terminal, rebooting when Windows asks:

```powershell
wsl --install --no-distribution
wsl --install --distribution Ubuntu-24.04
wsl --set-version Ubuntu-24.04 2
```

Install the pinned Linux toolchains, Git LFS, Flutter desktop prerequisites,
Steam, and Proton `10.0-4` inside that distribution: clang `18.1.3`, CMake
`3.28.3`, Ninja `1.11.1`, and GTK `3.24.41`. WSLg and a working virtual GPU
are required for the real Robotopia run. Preflight runs `git lfs fsck` and
rejects pointer-file checkouts on both Windows and WSL. All platform pins live
in `release/platform-toolchains.json`.

Configure absolute paths inside the WSL distribution:

```powershell
$env:TOPIAFORGE_PROTON_EXECUTABLE = "/home/release/.steam/root/steamapps/common/Proton 10.0/proton"
$env:TOPIAFORGE_STEAM_ROOT = "/home/release/.steam/root"
$env:TOPIAFORGE_COMPAT_DATA_ROOT = "/home/release/.local/share/topiaforge/compatdata"
```

The equivalent command parameters are `-ProtonExecutable`, `-SteamRoot`, and
`-CompatDataRoot`. `-WslDistribution` defaults to `Ubuntu-24.04`;
`-GameDirectory` identifies the current Windows Robotopia installation and is
translated into its WSL path by the orchestrator.

macOS packaging remains source-tested as future-platform capability, but RC1
has no macOS archive, remote builder, Apple identity, notarization step, or
macOS handoff manifest.

## Windows Creator evidence

The scripted Windows run verifies the installed build-2309 marker, Unity
lifecycle, canonical live markers, and packaged `new mod` to `dev` journey.
The nine interactive Creator workbench cases cannot be inferred from those
markers, screenshots, artifact-directory presence, a manually supplied cycle
number, or two identical arbitrary files.

Release is therefore deliberately blocked at this gate. The former
`new-windows-creator-evidence.ps1` pass synthesizer has been retired and now
fails closed. `release-admin.ps1` also rejects its legacy descriptor/bundle
schema, even if an internally consistent pair is supplied.

The gate may be re-enabled only after CreatorTools itself provides a bounded
native in-game result collector. That collector must generate an unpredictable
one-run challenge before launch, display and log it through the attributed
CreatorTools logger, and emit an explicit structured result for every
source-SHA case. Every result must bind:

- the challenge and exact `lastRunSessionId`;
- the acceptance and generated-journey package `sourceSha256` values and
  ordered critical-file receipts;
- the source SHA, version, Windows archive digest/size, canonical ecosystem
  digest, Robotopia build, and exact case-inventory digest;
- bounded typed evidence produced by the native action, not arbitrary file
  presence;
- measured lifecycle transitions and before/after save and checkpoint
  receipts from that same session.

Until that collector and its spoof, replay, wrong-session, wrong-challenge,
wrong-package, and arbitrary-file tests exist, a first prerelease cannot claim
the Creator acceptance gate. Completed unsigned build work may be retained,
but no Creator pass descriptor, handoff, tag, or release may be produced.

## Same-host WSL2/WSLg Proton evidence

The orchestrator, rather than an imported external descriptor, runs the exact
Linux archive through pinned Proton in the same workstation's WSL2
distribution. Configure the environment-backed absolute Linux paths documented
by `release-admin.ps1` for the Proton executable, Steam root, and dedicated
compat-data root. The runner creates a private wrapper that sets
`STEAM_COMPAT_DATA_PATH`, `STEAM_COMPAT_CLIENT_INSTALL_PATH`, and
`WINEDLLOVERRIDES=winhttp=n,b`, then uses only the packaged Linux CLI for the
release journey.

The generated descriptor is deterministic JSON and binds at least:

```json
{
  "schema": "release-proton-evidence-v1",
  "version": "1.0.0-rc.1",
  "targetSha": "0123456789abcdef0123456789abcdef01234567",
  "platform": "linux-proton",
  "executionEnvironment": "wsl2-wslg",
  "independentQa": false,
  "archiveSha256": "64-lowercase-hex",
  "archiveSize": 123456,
  "canonicalEcosystemSha256": "64-lowercase-hex",
  "gameBuildId": 2309,
  "protonVersion": "10.0-4",
  "protonRuntimeSha256": "64-lowercase-hex",
  "wineDllOverrides": "winhttp=n,b",
  "result": "pass",
  "suite": "full",
  "caseInventorySha256": "sha256-of-the-source-SHA-case-inventory-blob",
  "requiredCasesSha256": "sha256-of-the-sorted-required-case-set",
  "passedCasesSha256": "sha256-of-the-sorted-passed-case-set",
  "evidenceSha256": "sha256-of-the-retained-bundle",
  "evidenceSize": 654321
}
```

The runner hashes the complete canonical Proton runtime tree and the raw case
inventory blob from `targetSha`; it verifies every required case appears
exactly once, every result passes, the release journey package loads, and the
evidence bundle bytes match the descriptor. Do not put usernames, hostnames,
paths, timestamps,
credentials, or raw game logs in the deterministic descriptor. Both QA bundles
remain private local evidence; only their scrubbed digests enter the public
handoff. This is intentionally recorded as same-host, non-independent RC1
evidence.

## Staging and publication

Only a fully verified local build can enter `stage`. That phase creates or
verifies the signed annotated version tag, creates or resumes the exact draft
release, and uploads all catalog assets plus the two platform manifests, the
aggregate handoff manifest, and its detached
`release-handoff-v1.json.p7s` CMS signature.
The release author and every draft asset uploader must be the governance-pinned
`furroxide` user at immutable actor ID `221987073`; matching a mutable login
without the actor ID and `User` type is insufficient.
Existing assets are downloaded and byte-compared; replacement is forbidden.
An interrupted draft upload reported by GitHub as `state=starter` is the sole
exception: while the durable phase is still `built`, the orchestrator deletes
that exact asset ID and retries it. An `uploaded` byte mismatch always fails
closed.

`dispatch` first records a unique `release-admin-<32 lowercase hex>` request
ID in durable phase `dispatch-requested`. Immediately before every remote
workflow-dispatch call, it increments and durably writes
`finalizerDispatchAttempt`; there is no interval in which GitHub can accept an
unjournaled attempt. It then passes the same request ID to the protected
finalizer and binds the state to the one workflow run whose run name, version
tag, event, tag branch, and head SHA all match. Before trusting that run ID,
the orchestrator reads the repository-scoped Actions REST record and requires
the exact repository plus workflow path `.github/workflows/release.yml`; a
same-name workflow cannot substitute. The exact GitHub run ID is persisted
before the orchestrator waits for completion.

If an interruption leaves a journaled attempt without a run ID, `resume`
searches and waits through a bounded registration grace period. A run that
appears is bound without another dispatch. If no exact run becomes visible,
the command fails closed and instructs the administrator to resume later.
It never automatically redispatches a journaled request because GitHub's
`workflow_dispatch` API has no idempotency key. Never dispatch a replacement
run manually.

Approval of the GitHub `release` environment is the final human checkpoint.
While approval or execution is pending, `resume` finds and watches that exact
run. If it completed with `failure` or `cancelled`, `resume` uses GitHub's
rerun operation on the same run ID and verifies the rerun; it does not create
a second workflow-dispatch run. After approval, GitHub rechecks live release
governance, then verifies the tag, source, exact workflow/run/job provenance
for every required hosted check, draft, pinned CMS handoff signature, exact
timestamped Windows trust state, and QA evidence; generates update metadata, BOM,
SBOM, and checksums; creates a custom verifier attestation; publishes
automatically; and re-verifies the immutable release. Governance and candidate
identity are checked again immediately before publication.
The generated metadata names are the only assets permitted to identify
`github-actions[bot]` (actor ID `41898282`, type `Bot`) as uploader. If GitHub
returns performing-App metadata, it must identify GitHub Actions integration
ID `15368`. Catalog bytes, handoff manifests, and any local detached handoff
signature must continue to identify the pinned human staging principal.
Each hosted platform-verification record includes both the handoff digest and
the exact detached-P7S digest. The protected finalizer rehashes both draft
assets and rejects evidence from any other signature bytes.

GitHub does not support conditional requests for unsafe `PATCH` operations
unless an endpoint explicitly documents them, and the release-update endpoint
does not. The finalizer therefore holds the repository-wide publication
concurrency group, requires the exact sole-administrator governance policy, and
re-fetches the draft metadata and every asset immediately before its single
`draft:false` transition. Do not manually edit the draft after approving the
environment; this is the residual GitHub API limitation, not a recoverable
release step. See GitHub's
[conditional-request guidance](https://docs.github.com/rest/using-the-rest-api/best-practices-for-using-the-rest-api#use-conditional-requests-if-appropriate).

If a protected finalizer uploads policy-declared generated metadata and stops
before the single `draft:false` transition, the draft remains resumable.
Fetcher and local verification accept those exact generated names from the
pinned GitHub Actions bot, plus the pinned Actions App identity when GitHub
exposes it, while every admin-staged asset must still identify the pinned
human. Complete generated bytes are recomputed and compared; only an
incomplete Actions-owned generated upload may be repaired by the protected
publisher. No admin-staged starter or mismatched byte is replaceable.

The local phase changes to `published` only after the recorded run has
conclusion `success` and a second local verification proves that the exact
source SHA is now an immutable, non-draft prerelease with byte-identical
handoff assets. Running `dispatch`, `resume`, or `all` again from `published`
only re-verifies that run and release; it does not dispatch, rerun, upload,
rewrite state, or replace bytes. Any identity, byte, metadata, run, or release
mismatch fails closed.
