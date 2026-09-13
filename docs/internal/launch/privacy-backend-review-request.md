# Maintainer draft: RC1 remote AI, token and audio review

**Tracked maintainer draft — review and decision pending.** Prepared 2026-09-09 for `P0-PRIV-01` (blocking), for the project owner to forward manually.

Please review TopiaForge's optional RoboAPI integration for the proposed Windows x64 `0.1.0-rc.1` release and return an attributable decision within your authorized role. This request records no backend authorization, approved privacy notice, consent decision or release approval. The gate remains blocked pending actual review evidence.

## Review scope and role coverage

The reviewed release source is **`437733854795c11e684eac9d59d6bc52ada9516e`**, tree **`976326d02fd68b19a2e10bde923e15510df441a6`**; the commit-to-tree relationship was verified locally when preparing this request. The [reviewed-source and automated-verification summary](Evidence.md#reviewed-source-and-automated-verification) identifies that reviewed baseline. It is not a frozen final `main` candidate. Identify subsequent changes that require renewed review.

Required role coverage, in the gate's exact order:

| Required role | Requested contribution |
| --- | --- |
| `backend-owner` | Authorize the actual mod-layer endpoints and authentication use; supply accurate backend data-handling and operational policies. |
| `privacy-legal` | Review processing, jurisdiction, notice and consent requirements, and the owner-supplied policy text. |
| `product-owner` | Review player disclosures, activation/off controls, fallback behavior, accessibility and support ownership. |
| `robotopia-owner` | Confirm authority for Robotopia integration and reuse of the player's authentication token. |
| `security-owner` | Review credential handling, transport and redaction controls, abuse protections, incident ownership and acceptance gaps. |

Identify every role you cover and the authority or delegation supporting it. One person may cover several roles where authorized; project ownership alone does not establish other roles.

## Behavior and decisions to review

The [engineering disclosure](../../../docs/PrivacyAndCapabilities.md) describes the built-in HTTPS root `https://api.tomatocake.dev/v1`, with relative routes `/agent/check3` and `/agent/stt`. Authentication uses a bearer value from the player's bounded `robo_token.json` and a random per-session identifier. Development overrides require an absolute HTTPS root without credentials, query or fragment; redirects are disabled. Please explicitly authorize or decline these mod-layer calls, token reuse, and the supported override scope.

- **Brain and conversation:** `/agent/check3` receives mod-authored prompts/system framing, structured fields or decisions, current facts and a usage label; conversation also sends the current player line and compact earlier-turn transcript. Enabled prompt consumers may add a shared mod-authored directive to an already enabled request.
- **Speech:** `/agent/stt` receives gzip-compressed 16 kHz mono PCM audio, capped at 2 MiB after compression, following feature enablement and the documented push-to-talk action.
- **Defaults and fallback:** first-party live-brain, conversation and voice features default off. With all consumers off, no remote request, token-value read or audio capture occurs. Availability probes still check credential-file presence and enumerate microphone device names on mod load and scene change. Request failures must preserve deterministic gameplay; speech failure or cancellation returns no transcript and falls back to typed input.
- **Engineering bounds:** token-file reads are limited to 32 KiB, brain responses to 256 KiB and transcription responses to 64 KiB. Requests are bounded, cancellable and time-limited. Tokens and session headers must not appear in logs, diagnostic bundles, manifests, lock files or release metadata.

Please supply authoritative text and an explicit disposition for:

1. Destination/operator, purposes, data categories, authentication/account linkage, and the permitted use of each endpoint and any development override.
2. Retention periods and deletion processes for prompts, audio, transcripts/history, responses and associated logs/metadata; training use; geographic processing and jurisdictional requirements. Identify unknowns or unresolved owner questions. The repository supplies no approved retention or no-training assurance.
3. Monetary cost and who bears it, quotas/rate limits, abuse handling and escalation, and accountable owners for support and incident response.
4. Player-facing launcher disclosures and placement, including source, package SHA-256, arbitrary-code warning and aggregate dependency capabilities before install/update. Capabilities describe behavior; they neither sandbox a mod nor constitute consent.
5. Whether separate first-use consent is required, its exact wording and timing, platform microphone permission requirements, withdrawal and persistent off controls, and treatment of denied or revoked consent. Specify implementation changes required before approval takes effect.

Use the [exact blocker criteria](../../../docs/LaunchBlockers.md#p0-blockers) and the [privacy review criteria](ReviewGates.md) with the disclosure above.

## Acceptance matrix and evidence boundary

Please review the matrix and identify the evidence required for acceptance. Retained tests establish engineering behavior in their stated scope; they do not establish backend permission or native player acceptance.

| Required paths | Existing evidence and remaining review |
| --- | --- |
| Signed out, missing, denied and revoked credentials | Synthetic missing/oversized-token and invalidation coverage; actual loopback HTTP 401 followed by reloading a changed synthetic token file. This does not prove real account revocation or acceptance of all denied/signed-out player flows. |
| Microphone permission denied, no device, device loss and cancel | Native microphone, permission and fallback acceptance remains pending. Request cancellation tests do not establish capture cancellation behavior. |
| Offline and DNS failure | Synthetic unreachable-loopback behavior is covered. Dedicated DNS-failure and player-facing acceptance evidence is not established by that test. |
| TLS failure, redirects, HTTP 401/429/5xx | Retained HTTPS tests cover default-client certificate rejection, 301/302/303/307/308 refusal without destination connections, token reload after 401, and 429/500/503 fallback. Review scope and remaining native presentation. |
| Timeout, request/response oversize and cancellation | Retained synthetic tests cover stalled handshake timeout, caller cancellation, bounded reads and speech request caps. Review the exact coverage and require remaining native end-to-end evidence. |
| Mod unload or scene transition during capture/request | Native lifecycle, cleanup and deterministic fallback evidence remains pending in the admitted QA environment. |
| Logs, diagnostics and redaction | Synthetic client-log assertions cover token, session, transcript and authorization markers. Require the full matrix's diagnostic/log evidence, including secret-bearing URLs; do not extrapolate client-log checks to every output surface. |
| Disclosure, accessibility and off controls | Review actual notice/consent wording, keyboard-only operation, screen-reader labels and persistent off behavior. Native visual/accessibility acceptance and owner approval remain pending. |

The [retained HTTPS evidence](Evidence.md#roboapi-https-checks) documents **23 passing loopback HTTPS cases**, the passing rebuilt full manager harness, source hashes and fixture limitations. Tests used synthetic values and local servers; no real credential, live backend, trust-store entry or global TLS callback was used. This request neither reruns those tests nor closes the remaining microphone, scene, UX or accessibility work. Any live-service acceptance needs an expressly authorized account, endpoint and test scope.

## Requested response

Please return a signed or otherwise attributable private decision: **approve the stated scope**, **changes required before approval**, or **cannot approve**, with reasons, conditions, exclusions and unresolved items.

Recommended response fields, usable in your normal private review system:

- Project/release and gate (`TopiaForge 0.1.0-rc.1`, `P0-PRIV-01`); exact source/tree, documents, endpoints and behavior actually reviewed.
- Reviewer identity, role(s), authority/delegation basis and UTC decision time.
- Decision and scope; authoritative policy/consent text or durable references; endpoint/token authorization; named policy, support and incident owners.
- Required code/disclosure changes, missing acceptance evidence, conditions and re-review triggers for source, service, policy or candidate changes.
- Supporting private references and hashes where available, attributable approval trail and record custodian.

These are review-record recommendations, not an enforced attestation schema. No approval or evidence ID is prefilled. Keep personal information, credentials, player transcripts and internal review text out of chat, tracked files and public release assets; provide safe record references instead.

After actual role coverage and decisions are verified, a maintainer can propose a separately reviewed gate update under the [existing record boundary](ReviewGates.md#review-sequence-and-record-boundary). Unmet conditions remain open. This decision does not approve other gates, replace native acceptance or authorize candidate construction/publication.

Preparation decisions and pending authorization are recorded in [Decisions](Decisions.md); see [NextActions](NextActions.md) before execution. Questions require an explicit reply, with no timeout-based default.
