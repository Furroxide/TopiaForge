# Release ownership and incident operations

The interim owner for the first TopiaForge release line is repository administrator `@furroxide`. Before
`1.0.0-rc.1` can be published, that account must confirm that GitHub notifications and private vulnerability reports
are monitored and name a delegate for any role it cannot cover.

| Responsibility | Intake and authority | First-RC expectation |
| --- | --- | --- |
| Public support | GitHub issues; `@furroxide` triages or delegates | Best effort; no response-time or LTS promise. |
| Security intake | GitHub private vulnerability reporting; `@furroxide` coordinates | Keep reports private until a fix/advisory window is agreed. |
| Release manager and notes | `@furroxide` | Reviews the exact candidate evidence and records the final ship/no-ship decision. |
| Release incident commander | `@furroxide` or a named delegate | Pauses publication and coordinates advisory, replacement, and user communication. |
| Package trust, revocation, and takedown | `@furroxide` with security/product review | Records affected identities/hashes and the recovery path before changing indexes. |
| Rollback | Release manager | There is no in-place rollback for the initial immutable release; ship a new version or advisory. |

## Incident procedure

1. Stop draft publication and preserve the affected tag, artifact hashes, logs, and attestations. Never replace an
   immutable asset or move/delete a protected version tag.
2. Classify impact across the loader, launcher, SDK, mods, VPM packages, registries, game compatibility, credentials,
   and user data. Rotate exposed credentials immediately through their owning provider.
3. If an unpublished candidate is affected, keep it blocked and cut a new RC version after remediation. If a public
   release is affected, publish a GitHub advisory and a new immutable version; mark affected registry/package entries
   according to the approved trust policy rather than silently rewriting history.
4. Give installed users concrete safe-mode, disable, uninstall, or repair steps and identify whether saves or synced
   multiplayer state are affected.
5. Attach the timeline, decision owner, evidence, and follow-up work to the release record. Legal/privacy/security
   incidents require their respective approver before closure.

The first-RC support gate remains open until the owner confirms monitoring and the legal/privacy/trust policies in
`LaunchBlockers.md` are approved.
