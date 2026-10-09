# Maintainer checklist: update-signing recovery and cleanup

**Tracked maintainer checklist prepared 2026-09-09; execution and completion pending.** This checklist is for the project owner to use with authorized administrators. Preparing it authorizes no key access, recovery test, signing, workflow run, secret change or deletion.

The historical [P0-HOST-01 entry](../../../docs/LaunchBlockers.md#p0-blockers) reports a GitHub-held update key and a plaintext workstation duplicate. The duplicate's current existence and the identity of the protected secret's contents have not been established. A September 9 name-only observation found the update-signing secret name; it establishes neither key identity nor recoverability. This housekeeping does not close the separately deferred `P0-CRED-01` exposure review or the rest of `P0-HOST-01`.

## 1. Agree the recovery scope and preserve the current key

- [ ] Identify the existing key custodian and GitHub administrator. **Recommended: name a second authorized reviewer** for independent recovery and confirmation. Record authority, test scope and evidence custodian. References below to a second reviewer describe this recommended arrangement; existing policy imposes no new reviewer-role or person-count rule. If another method is used, document how it establishes independent verification.
- [ ] Establish the expected **public** Ed25519 key from the independently reviewed launcher trust source and applicable release policy. Retain the source revision, public key, full public-key SHA-256 and key ID. Never use the seed, seed hash or private-key contents as an evidence identifier.
- [ ] Identify an approved protected backup/escrow location and its independently usable recovery procedure. Record safe location references, access controls, authorized recovery personnel and any required unlock/quorum process.
- [ ] If there is no approved independently recoverable copy, stop cleanup. Arrange separately authorized protected backup/escrow provisioning with the custodian. **Do not remove the sole recoverable copy or silently generate a replacement key.** A different key requires a separately reviewed trust-root rotation.

The [administrator guide](../../../docs/AdminRelease.md#signed-or-unsigned-distribution) requires Ed25519 update signing even for unsigned Windows RC1. Its protected secret is `TOPIAFORGE_UPDATE_ED25519_PRIVATE_KEY_B64`.

GitHub's environment-secret read API returns metadata without revealing the encrypted value; it is not a seed-export or backup-recovery interface. Recover from the approved protected backup/escrow, not an attempt to extract the Actions secret. [GitHub environment-secret documentation](https://docs.github.com/en/rest/actions/secrets#get-an-environment-secret).

## 2. Independently prove recovery from the approved backup

- [ ] Obtain separate authorization for the exact recovery source, controlled test host, tool/version and nonpublishing challenge test. Keep the original local duplicate out of this recovery path so it cannot accidentally supply the result.
- [ ] Have the authorized operator recover through the approved backup/escrow procedure, with the second reviewer checking the source and method. Keep seed material within the approved protected execution/storage path; exclude it from command arguments, shell history, transcripts, logs, screenshots and artifacts.
- [ ] Derive the public key from the recovered seed and compare the **complete public key** and its public fingerprint to the agreed baseline. Stop on mismatch; do not update launcher trust or the GitHub secret to make the test pass.
- [ ] Sign an unmistakable recovery-only challenge containing a fresh nonce, UTC time, repository identity and purpose. It must not be valid release/update metadata. Retain only the public challenge bytes, signature and public identity.
- [ ] Have the second reviewer independently verify that signature against the expected public key and exact challenge bytes, and confirm that altered challenge bytes fail verification. Record tools/versions and outcome.

The [existing signing implementation](../../../packages/launcher_data/lib/src/launcher_update_trust.dart) provides `fromSeed`, public-key derivation and signing primitives. A later reviewed harness can use those and raw Ed25519 verification for the challenge; this checklist supplies no executable key-handling command and invokes no key-generation path.

## 3. Independently verify the protected GitHub configuration

- [ ] Review safe configuration evidence: repository/environment identity, secret **name and metadata only**, `release` environment protections and allowed refs, applicable approvals/bypass policy, and the exact reviewed workflow revision. Confirm secret access stays within protected jobs and PR/dry-run jobs remain secretless. See [repository governance](../../../docs/RepositoryGovernance.md#trusted-release-and-deployment-environments).
- [ ] Treat secret-name presence and metadata as configuration evidence only; they cannot establish which signing key is stored.
- [ ] When prerequisites and protected-environment approval permit, prepare and separately authorize a reviewed **nonpublishing** verification that uses the protected secret to sign a distinct recovery-only challenge and exposes only public proof. The second reviewer must verify it against the same baseline public key and compare it with the recovered-backup identity.
- [ ] Do not dispatch the [release workflow](../../../.github/workflows/release.yml) merely to test the secret while release gates remain blocked. Do not create a release/tag, publish metadata, broaden secret access or overwrite the existing secret as part of this checklist.

If protected-secret identity cannot yet be proved, record that dependency and leave cleanup authorization pending. Neither a local recovery test nor historical configuration notes substitute for this independent check.

## 4. Prepare exact cleanup targets before asking for deletion approval

- [ ] Retain the successful recovery proof, protected-secret identity proof and second-reviewer decision in the approved private record. Confirm the protected backup remains recoverable and accessible to authorized custodians after cleanup.
- [ ] Under a separately authorized metadata inspection, enumerate each intended plaintext duplicate by its **resolved absolute path**, host, file type, owner/ACL and relevant backup/sync replicas. Check links/reparse points and containment. Do not open or print secret contents.
- [ ] Reconcile backup, recycle-bin, sync/version-history and retention requirements with the custodian. Identify each additional plaintext copy as a separate exact target; preserve the approved protected recovery source.
- [ ] Present the finite target list, current access controls, retained recovery locations, deletion method, storage/sync limitations and evidence-retention plan for **separate explicit deletion authorization**. Do not use broad wildcard/recursive cleanup or infer approval from preparation of this document.

## 5. Execute only the separately approved cleanup and record closure

- [ ] After authorization, recheck target identity and scope, then remove only the approved duplicates using the approved method. Stop if paths, recovery status or protected configuration changed.
- [ ] Verify the intended targets' absence without reading key contents, confirm recovery references and protected configuration remain intact, and record any remaining replica/retention work. File absence alone is not proof of storage sanitization.
- [ ] Obtain the second reviewer's attributable completion record. Leave incomplete steps pending; do not assign approval/evidence IDs or mark a release gate closed from this checklist alone.

Return the completion record through the agreed private channel; only a verified safe reference belongs in tracked status. Suggested private completion record: authorizations and operators/reviewers; UTC times; reviewed source/workflow revisions; expected public identity; backup/escrow reference; challenge/signature/verifier results; protected configuration proof; exact approved cleanup targets and outcomes; retained recovery/evidence locations; unresolved dependencies. These are recommended fields, not an enforced schema. No private seed, seed digest or secret-bearing log belongs in that record.

Preparation decisions and pending authorization are recorded in [Decisions](Decisions.md); see [NextActions](NextActions.md) before execution. Questions require an explicit reply, with no timeout-based default.
