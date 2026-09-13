# Maintainer setup checklist: governance audit token

**Tracked maintainer instructions prepared 2026-09-09; configuration remains pending.** The authorized repository administrator performs token creation and secret entry privately in GitHub. This document contains no credential value and authorizes no release dispatch.

## Required configuration

The [administrator runbook](../../../docs/AdminRelease.md#signed-or-unsigned-distribution) and [governance policy](../../../docs/RepositoryGovernance.md#trusted-release-and-deployment-environments) define this credential:

| Setting | Required value |
| --- | --- |
| Credential type | Dedicated fine-grained personal access token |
| Resource owner | `Furroxide` |
| Repository access | Only selected repositories: `TopiaForge` |
| Repository Administration | Read-only |
| Repository Actions | Read-only |
| Metadata | Read-only, implicit |
| Other permissions | No additional account/organization/repository grants; no writes |
| GitHub environment | Existing protected `release` environment in `Furroxide/TopiaForge` |
| Environment-secret name | `TOPIAFORGE_GOVERNANCE_AUDIT_TOKEN` |
| Expiration | Administrator-selected bounded lifetime and a recorded renewal date; the repository specifies no fixed number of days |

The audit token performs protected governance reads. The workflow's separate `GITHUB_TOKEN` handles verification, attestation and publication. Keep the broad interactive maintainer credential separate, and do not substitute a short-lived installation token for this environment secret. Existing Ed25519 signing-key recovery is another task.

## Create the dedicated token privately

1. Sign in as the authorized repository owner/administrator. In personal Settings, open Developer settings → Personal access tokens → Fine-grained tokens → Generate new token. The [fine-grained token settings](https://github.com/settings/personal-access-tokens) provide the entry point.
2. Use a descriptive name, for example `TopiaForge governance audit`. Select the owner, single repository, permissions and expiration above; review the complete permission summary before generating.
3. If the owner/repository cannot be selected, resolve account authorization first. A PAT cannot provide rights its owner lacks. Complete any approval GitHub requires before relying on the token. Follow [GitHub's token-creation instructions](https://docs.github.com/en/authentication/keeping-your-account-and-data-secure/managing-your-personal-access-tokens#creating-a-fine-grained-personal-access-token).

The administrator should perform the generation and transfer without screen sharing or assistant screenshots of the revealed value. Enter it directly into GitHub's environment-secret field. Keep it out of chat, command arguments/history, tracked or temporary plaintext files, logs and repository content. Handle any private retention through the administrator's approved credential-storage process.

## Add the protected environment secret

Open [TopiaForge environment settings](https://github.com/Furroxide/TopiaForge/settings/environments), select the existing `release` environment, then under Environment secrets choose Add secret. Enter the exact name `TOPIAFORGE_GOVERNANCE_AUDIT_TOKEN`, paste the generated value privately, and save. Follow [GitHub's environment-secret instructions](https://docs.github.com/en/actions/how-tos/write-workflows/choose-what-workflows-do/use-secrets#creating-secrets-for-an-environment).

Retain the existing required reviewer, `v*` tag restriction and disabled administrator bypass required by repository policy. No environment-protection changes are part of this task. If the expected protected environment is unavailable, resolve that with the repository administrator before storing the credential.

## Confirm completion without revealing the token

The administrator retains a private, attributable configuration record containing the token's descriptive name/reference, owner, selected repository, exact permission levels, expiry, renewal owner/date, and the saved environment-secret name/time. A settings record should show permission metadata only, with no revealed token. These are safe verification fields, not a new enforced attestation schema.

The following optional read-only command uses the administrator's existing GitHub CLI authentication and lists only the matching secret's name and update time:

```powershell
gh secret list --repo Furroxide/TopiaForge --env release --json name,updatedAt --jq '.[] | select(.name == "TOPIAFORGE_GOVERNANCE_AUDIT_TOKEN") | {name, updatedAt}'
```

Report `configured` through the agreed private channel with a safe configuration-record reference. The maintainer can then repeat the name-only check. Secret-name presence cannot prove the encrypted credential's permissions, expiry or successful governance access; the administrator record and later protected governance checks provide the remaining evidence. No release workflow should be dispatched merely to test this setup while release prerequisites are blocked.

The dated September 9 name-only observation found the audit-token name absent and the separate update-signing secret present; see [preparation evidence](Evidence.md). Recheck configuration when the administrator reports completion. Preparing this guide does not change that observation, rotate previously exposed credentials, close `P0-CRED-01`, qualify a candidate or approve publication.

Preparation decisions and pending authorization are recorded in [Decisions](Decisions.md); see [NextActions](NextActions.md) before execution. Questions require an explicit reply, with no timeout-based default.
