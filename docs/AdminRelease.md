# Admin-orchestrated release

Production archives are built only on maintainer-controlled machines. GitHub
Actions verifies the exact staged bytes, attests what it verified, creates the
signed update metadata, and publishes after approval of the protected
`release` environment. It does not rebuild production packages.

The entry point is:

```powershell
$releaseStateRoot = 'C:\QA\TopiaForge\release-state'
$isolationRecord = 'C:\QA\TopiaForge\isolation.json'
$sourceGameRoot = 'C:\QA\TopiaForge\source-game'
./tools/release-admin.ps1 preflight -StateRoot $releaseStateRoot -AcceptanceIsolationRecord $isolationRecord -GameDirectory $sourceGameRoot
./tools/release-admin.ps1 build -StateRoot $releaseStateRoot -AcceptanceIsolationRecord $isolationRecord -GameDirectory $sourceGameRoot
```

`build` contains a mandatory interactive window: the live `TF-ACCEPT`
acceptance run needs roughly 30 minutes at the keyboard with a gamepad and
microphone. See [`LiveGameAcceptance.md`](LiveGameAcceptance.md).

A successful build stops at `built`. Prepare and review the detached candidate
decision and acceptance records described below, then continue explicitly:

```powershell
./tools/release-admin.ps1 qualify -StateRoot $releaseStateRoot
./tools/release-admin.ps1 stage -StateRoot $releaseStateRoot
./tools/release-admin.ps1 dispatch -StateRoot $releaseStateRoot
```

The durable phases are `preflight → platforms-built → built → accepted → staged
→ dispatch-requested → published`. `resume` and `all` continue only through
already authorized phases: both stop at `built` with instructions to run
`qualify`. Neither command grants acceptance. Add `-Rehearsal` to `all` for a
verified, non-publishing rehearsal of every platform in `artifactPolicy`. A
rehearsal can never qualify, create a tag, stage assets, or dispatch publication.
Local state and raw evidence live under `<StateRoot>/<version>/`. The default
`.release-local/` is ignored by Git, but cannot supply isolated acceptance output
when the checkout is inside the normal user's profile. Use the same explicit
external `-StateRoot` for every phase, including `resume`. Provision and restrict
access to the example paths before using them; the example does not create a QA
account or approve its layout.

Each canonical ecosystem pass runs in its own detached clean worktree. The
SHA-256 of the normalized, sorted tree manifest is the ecosystem identity; the
canonical tar has a separate transport digest. Every platform validation summary
binds both values. Windows-only RC1 does not run a WSL/Proton acceptance path.
The future Linux prerequisites below must be implemented before a reviewed
policy can enable a Linux candidate.

## Private build and exact candidate qualification

Preflight invokes `release validate-prerequisites --version <version>
--target-sha <final-main-sha>`. It reads `release/release-readiness.json` and its
schema from that exact commit. All twelve gates remain represented. The four
blocking non-game gates (`P0-IP-01`, `P0-OSS-01`, `P0-PRIV-01`, and `P0-CRED-01`)
must already be approved. Only `P0-GAME-01` may await the candidate's live
acceptance. Success means `eligible-for-private-build`; it is not a ship
approval. Advisory gates retain their recorded enforcement and reporting rules.

The source catalog's `ready` status approves only the reviewed platform, package
and version inventory. It does not supply those four review records or certify
unbuilt archives. Exact payload checks and final acceptance remain mandatory.

Obtain the final source through the policy-approved same-repository release PR
into `main`. Preserve its two-parent merge and required hosted checks; freeze
the final `main` merge SHA, not the release-branch head. Local `main` must remain
clean and exactly equal to `origin/main`. Moving that source invalidates the
candidate even when a later commit has the same tree.

`build` still requires the actual SDK, authored-world and live-game checks
before sealing its validated handoff. Run live acceptance in an isolated Windows
user/session or virtual machine that isolates Unity's persistent data and does
not access the normal user's data. A separate BepInEx profile alone is
insufficient. Pass the already approved private provisioning record through
`-AcceptanceIsolationRecord`; it is forwarded to the packaged CLI's
`--isolation-record`. Preflight freezes its exact path and bytes; resume cannot replace that input.
Keep the record outside all output/evidence directories that the build clears.
Linked output directories and linked existing ancestors are rejected before cleanup.
The record identifies a separate QA game installation, launcher/output roots and
measured primary-token/known-folder identity. The build/acceptance process must already be
in that isolated Windows user/session or VM. Its provisioning `outputRoot` must
exactly equal `<StateRoot>/0.1.0-rc.1/evidence/windows/robotopia`. Authenticated
preflight may run as the release operator, followed by the frozen-state build in
the QA session and qualification/publication back in the operator session. Use
the same controlled checkout and state paths; do not copy GitHub credentials
into the QA profile. Pass `-GameDirectory` explicitly at preflight: it must name
the verified source installation in the record's `sourceGameRoot`, accessible to
both sessions and separate from the record's admitted QA `gameRoot`. Its frozen
path persists across sessions; omitting it selects the operator's default local
installation. Missing provisioning fails closed;
no tool creates a user account or imports normal-player saves automatically.

The private schema-3 `acceptance-result.json` retains the actual acknowledgement
and byte hashes of both the provisioning record and runtime acknowledgement,
plus confirmed owned-process exit. Its `gameDirectory` names the admitted QA
installation; last-run evidence comes from that installation. Public candidate
acceptance records carry only the isolation kind and reviewed proof digest,
not usernames, paths, SID or the raw acknowledgement. Once those exact bytes and private
evidence exist, place these reviewed, schema-valid records in the candidate's
assets directory (`<StateRoot>/<version>/assets`):

- `release-candidate-readiness-v1.json`: the detached final decision, bound to
  the frozen source and tracked contracts. Only the game gate may supersede its
  tracked base decision; the other gates retain their reviewed values.
- `release-candidate-acceptance-v1.json`: the actual acceptance evidence for that
  same candidate, with the required cases and reviewer references. Keep raw
  logs, personal paths, credentials and other private evidence outside public
  assets. Do not fabricate passing cases or approval references.

Use the schemas from the frozen commit. `qualify` invokes
`release validate-readiness --version <version> --target-sha <final-main-sha>
--assets <assets-directory>`. The validator binds the decision, acceptance,
handoff, sorted payload names/sizes/SHA-256 values, and exact tracked readiness,
schemas, policy and catalog. It must return `ready`. The administrator tool then
atomically records the complete normalized assessment and its hashes in
`accepted` state. An interrupted write preserves the preceding state; retrying
the same qualification is safe and does not rewrite an existing acceptance.

`stage`, `dispatch` and `resume` revalidate that frozen assessment, source,
receipts and handoff. Changed payloads, evidence, decision, contract or source
fail closed. Accepted bytes cannot be rebuilt or repacked through `build`.
Keep superseded candidates for diagnosis; prepare a new candidate and obtain
fresh acceptance when any bound input changes. Publication metadata is derived
from the qualified inventory afterward; it is not part of its own payload hash
inputs.

Machine validation checks evidence structure and binding. It does not prove a
reviewer's identity or permission to approve. Actual reviewer authorization,
evidence inspection and the protected `release` environment approval remain
human responsibilities.

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
  `10.0.26100.0`, Python 3.11 or newer, Git LFS, 7-Zip, tar, `jq`, `bash`
  (Git for Windows), WSL, and GitHub CLI;
- an activated local Unity license and the Robotopia build-2409 installation.

On Windows systems where `python` resolves to the nonfunctional Microsoft
Store alias, set `TOPIAFORGE_PYTHON` (or pass `-PythonPath`) to an absolute,
working Python 3.11-or-newer executable. Preflight executes a version probe; a
path existing on disk is not sufficient.

The shell release verifiers parse GitHub API responses with `jq` and stop
without it. `release-admin.ps1` resolves `jq` before any transaction phase
begins, so a missing `jq` fails immediately instead of part-way through a
release. If `jq` is not on `PATH`, the installation directories used by
`winget`, Chocolatey, and `%ProgramFiles%\jq` are searched, and the resolved
directory is prepended to `PATH` for the shell verifiers only. Install it with:

```powershell
winget install jqlang.jq
```

## Build from a neutral root

Dart and Flutter binaries can retain absolute paths from the source checkout,
SDK and pub cache. A neutral checkout alone does not prevent the administrator's
account name or folder layout from entering an artifact. `subst`, a junction or
an FVM link back into a personal profile does not establish physical isolation.
Stripping debug information and running `flutter clean` are not substitutes for
preparing all three inputs under physical paths that identify no account.

Provision the following before running any administrator release phase. These
example paths describe the required layout; they do not indicate an existing
installation or authorize a build:

| Input | Required preparation |
| --- | --- |
| Source checkout | A real, clean `main` checkout exactly equal to `origin/main`, with hydrated Git LFS files, for example `C:\TopiaForgeBuild\source`. Retain its Git metadata and source identity. |
| Transaction worktrees | Use a physical neutral state directory, for example `-StateRoot C:\TopiaForgeBuild\state`, consistently for every phase. The default is `.release-local` under the checkout; it is neutral only when that checkout is neutral. The administrator creates exact-SHA build worktrees below `<StateRoot>/<version>/worktrees`. |
| Flutter and Dart SDK | Install a fresh official pinned Flutter `3.44.6` SDK, including its Dart `3.12.2`, at a physical neutral location such as `C:\TopiaForgeBuild\flutter-3.44.6`. Do not copy a personal SDK's generated caches. |
| Pub cache | Set `PUB_CACHE` to a fresh, dedicated neutral directory, for example `C:\TopiaForgeBuild\pub-cache`. Populate it through the checked-in lockfiles, not by copying a personal cache. |

`release-admin.ps1` resolves Flutter and Dart through
[`tools/flutter-sdk.ps1`](../tools/flutter-sdk.ps1): it prefers
`.fvm/flutter_sdk/bin` in the administrator checkout, then `PATH`. Ensure any
preferred FVM SDK resolves to the prepared neutral SDK; otherwise use a clean
checkout without that link and prepend the neutral SDK's `bin` to `PATH`.
Changing `PATH` cannot override an existing preferred FVM installation. The
administrator passes the selected commands to `build-windows.ps1` as `-DartPath`
and `-FlutterPath`; the child builds inherit `PUB_CACHE`.

Configure `PATH` and `PUB_CACHE` in a dedicated release PowerShell process.
Do not reassign `HOME` or `USERPROFILE`, import credentials or launcher profiles,
or copy personal SDK/pub caches into the build layout. Keep the isolated game
installation and its provisioning record separate, as required by
[Live Game Acceptance](LiveGameAcceptance.md). Neutral compiler paths do not
satisfy the game-isolation or approval prerequisites.

Start from fresh source worktrees, without reused `.dart_tool`, build outputs or
package configurations from another location. The existing administrator path
already restores the canonical CLI with `pub get --enforce-lockfile`. The
Windows builder cleans Flutter output, restores its locked dependencies, and
removes the CLI's `.dart_tool` before its locked restore and AOT compilation.
These steps prevent stale source paths but depend on the SDK and cache prepared
above. Version checks do not verify that those paths are neutral.

`Directory.Build.props` continues to map repository .NET source paths to `/_/`.
It does not rewrite every SDK or precompiled native input. The final
`release test-package` scan remains the enforcement boundary for embedded host
paths; the administrator scripts do not automatically provision or relocate
neutral SDK/cache roots. A scan failure stops the candidate and requires a
corrected build environment, not a broader scanner exception.

Hosted dry runs use `tools/prepare-neutral-build-root.sh` plus separate neutral
SDK/cache preparation. That helper deliberately copies only tracked files and
excludes `.git`; its output cannot replace the administrator's final-`main`
checkout or the exact-SHA transaction worktrees. A successful hosted dry run
therefore does not establish that the private administrator layout is ready.
Any future platform build must also prepare its source, SDK and cache roots;
a neutral Linux checkout path alone is insufficient.

## Signed or unsigned distribution

`signingIdentities.windowsDistribution` in `release/release-policy.json` records
which one this candidate is. The key is optional and its absence means `signed`,
so a certificate that simply went missing can never be read as a decision to
ship without one — shipping unsigned has to be written down.

`unsigned` is accepted by policy validation only on a `0.x` prerelease and only
when no certificate is pinned; `release validate-policy` rejects a policy that
carries both. Unsigned Windows RC1 is authorized, and the checked-in policy now
records `windowsDistribution: unsigned`. The construction repairs are implemented
and their synthetic regressions pass. Final source CI, including Windows/Linux
tests and documentation publication, passes; revision-specific evidence is in
[`gamemode-contract/Status.md`](internal/gamemode-contract/Status.md).
No candidate is qualified. Building the exact frozen payloads, isolated live
acceptance and remaining gate approvals are still required.

The validator, handoff and qualification contracts require verified unsigned
executable evidence in this mode. Both the detached CMS asset and its decision
digest must be absent, while handoff, qualification and payload digests remain
mandatory. Missing signing credentials never select this mode automatically.
Ed25519 update signing, candidate qualification and protected publication approval
remain required in either mode. Windows may show an unrecognized-publisher or
SmartScreen warning for unsigned artifacts; this is not a verified publisher claim.

The following certificate requirements apply when the policy selects `signed`
(including when the optional distribution field is absent). Before freezing
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

## Future Linux acceptance

RC1 includes only the Windows x64 archive. Linux/Proton acceptance is currently
unavailable: `tools/release/test-proton.sh` is a refusing stub, and
`release-admin.ps1` rejects a policy containing `TopiaForge-linux-x64.zip` during
preflight and build. Re-adding that archive to `platformArchives` does not restore
the retired runner or make its historical evidence valid.

A future Linux candidate requires a reviewed native isolation implementation
that proves the runtime identity and persistent-data boundary before staging or
launch, integration with exact-candidate evidence verification, and actual
acceptance on the supported game/rendering configuration. A Wine prefix, WSL
distribution or alternate BepInEx profile alone does not establish that boundary.
Review the isolation and evidence implementation before separately enabling the
platform in policy for private candidate construction. The resulting exact bytes
must then pass actual acceptance before qualification or publication. No later
release version or Linux approval is implied by retained platform pins or
successful hosted packaging tests.

The earlier WSL2/Proton setup, descriptor and schema2 acceptance commands are
historical material, not runnable release instructions. See the
[isolation implementation handoff](internal/gamemode-contract/prompts/08b-isolated-acceptance.md)
and [revision-specific evidence](internal/gamemode-contract/Status.md) for the
retirement decision. Any future same-host evidence must disclose that it is
non-independent; it cannot be presented as independent QA.

macOS packaging remains tested as future-platform capability, but RC1 has no
macOS archive, remote production builder, Apple identity, notarization step, or
macOS handoff manifest.

## Staging and publication

Only an `accepted` candidate can first enter `stage`. That phase creates or
verifies the signed annotated version tag, creates or resumes the exact draft
release, and uploads the strict allowlist: catalog assets, the platform
manifests required by policy, the aggregate handoff, both detached candidate
qualification records, and `release-handoff-v1.json.p7s` only for a signed
Windows distribution.
The release author and every draft asset uploader must be the governance-pinned
`furroxide` user at immutable actor ID `221987073`; matching a mutable login
without the actor ID and `User` type is insufficient.
Existing assets are downloaded and byte-compared; replacement is forbidden.
An interrupted draft upload reported by GitHub as `state=starter` is the sole
exception: while the durable phase is still `accepted`, the orchestrator deletes
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
for every required hosted check, draft, detached qualification, declared Windows trust state (including the
pinned timestamped CMS and Authenticode signatures when signed), and QA evidence; generates update metadata, BOM,
SBOM, and checksums; creates a custom verifier attestation; publishes
automatically; and re-verifies the immutable release. Governance and candidate
identity are checked again immediately before publication.
The generated metadata names are the only assets permitted to identify
`github-actions[bot]` (actor ID `41898282`, type `Bot`) as uploader. If GitHub
returns performing-App metadata, it must identify GitHub Actions integration
ID `15368`. Catalog bytes, handoff manifests, both detached candidate qualification records,
and any local detached handoff signature must continue to identify the pinned
human staging principal. Each hosted platform-verification record binds the
handoff and qualification digests. Signed distributions additionally bind the
exact detached-P7S digest; unsigned distributions explicitly omit that signature.
The protected finalizer rehashes the declared draft assets and rejects changed
qualification or trust evidence.

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
