# Slice 8b: isolated acceptance and release evidence

Begin only after slice 8a merges into `dev`. Read the [canonical brief](../../GamemodeContractRedesign.md),
[evidence ledger](../Status.md), and [common rules](README.md). Refresh remote
state and create this delivery branch from the merged prerequisite. Reuse the
preserved, reviewed source without assuming its earlier tests certify this revision.

## Deliverables

- Require an explicit reviewed provisioning record for an existing isolated Windows
  user/session or VM. Do not create accounts, copy ordinary saves or authenticate
  as another user. Measure primary-token SID, logon/session identity and OS known
  folders; environment-variable redirection does not prove native isolation.
- Admit the actual isolated game, loader, manager, launcher, output and persistent
  roots before installation/configuration/staging/log/output writes. Reject links,
  device/alternate-stream/trailing-dot/space aliases and filesystem-resolved aliases
  in roots and existing ancestors; preserve normal Windows case equivalence.
- Keep public profile transport V4 unchanged. Bind the private expiring request and
  write-once runtime ACK to exact profile bytes, request ID, random challenge,
  provisioning-record hash, actual native receipt and measured runtime roots.
  Create no manager state or global UI effects when admission fails. Consume and
  retain the accepted V4 snapshot once; never reread a changed profile for startup.
- Retain the original process/thread ownership from suspended Windows creation.
  Check the child identity before resume. Stop only that receipt; after uncertain
  exit retain evidence/layout and report unconfirmed. Inject identity-read and
  resume failure after allocation, proving all handles and owned children drain.
- Use private acceptance-result schema3 with exact ACK/record hashes, request-time
  correlation and confirmed exit. The read-only verifier must reject unknown and
  duplicate fields, malformed types, stale/mismatched proofs and altered bytes.
  Keep private identities/paths out of public qualification artifacts.
- Require/freeze explicit `-AcceptanceIsolationRecord` in release administration,
  preserve it outside directories cleared by the build, and revalidate on resume.
  Forward it to CLI; use the admitted isolated manager's last-run log. Retire
  executable schema2 acceptance readers and fail Proton before staging or launch
  until native isolation is implemented. Preserve source/signing/qualification gates.
- Update the active acceptance/admin guides, CLI help and this evidence ledger
  together with their actual commands. Unsigned Windows RC1 is already authorized;
  Ed25519, provenance, checksums, real approval records and exact-byte qualification
  remain required. Do not prefill approval or publish a rehearsal candidate.

## Regression and verification matrix

Add each confirmed defect's regression before its fix. Include linked loader and
mutable-tree ancestors, aliases/case equivalence, duplicate/scalar coercion,
source/profile/ACK byte changes, expiry and evidence intervals, child identity,
PID reuse, late exit, missing ACK, partial/throwing cleanup, consumed-profile ACK
commit failure, and denied plugin startup/teardown without global UI effects.
Record harmless-child tests separately from engine/native timing evidence.

Run applicable AGENTS checks, a fresh Release build and all seven no-argument C#
harnesses, CLI/data suites and fatal-info analysis, formatter/line caps, release
PowerShell aggregate and analyzer, shell syntax, repository audits, full publication,
Flutter tests/analyzers and Windows build. Register new suites in normal CI.

## Actual acceptance and handoff

Use the admitted isolated installation and exact immutable candidate bytes. Record
source/package/game identity, command, expected/actual scene/world/spawn behavior
and retained logs for cold launch; Open Sandbox geometry/environment/kill-plane;
both discovered sources; root/inactive/ambiguous authored markers; Zombies; Sandbox
F5/pause; Free Play with Sandbox absent; restart and main-menu return.

Inject allocation then startup failure, cancellation in every phase, late native
completion, throwing teardown, owner unload, stale callbacks and competing scene
requests. Verify ownership release, exactly one terminal outcome and later launch.
Run the template's EditMode tests using the pinned editor. Do not substitute the
installed game's Unity runtime for that editor or infer visuals from log success.

Completion requires ordinary protected integration, updated public references,
real approval records and all required live evidence. When actual QA access,
reviewer records, editor or visual/native control are unavailable, record each
pending case explicitly; finish authorized software work without claiming release
readiness, creating a release tag, or dispatching publication.
