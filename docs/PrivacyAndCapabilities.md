---
title: Privacy and capability disclosure
description: Understand full-process trust and sensitive behavior declarations.
---

# Privacy and capability disclosure

Status: engineering disclosure for the initial-release candidate. This document does **not** replace an approved
privacy notice, backend authorization, or platform microphone-consent text. Those approvals remain release blockers.

TopiaForge mods are trusted in-process C# code. Manifest capabilities explain potentially sensitive behavior to a
player; they do not sandbox, mediate, or grant that behavior. Install only packages whose author and source you trust.

## Canonical capabilities

| Capability | Meaning |
| --- | --- |
| `network` | Opens outbound network connections. |
| `remote-ai` | Sends inputs to a remote inference service and consumes its response. |
| `player-token` | Reads the player's Robotopia authentication token for an explicitly enabled integration. |
| `microphone` | Captures audio from a local microphone after an explicit player action. |
| `speech-to-text` | Sends captured audio to a remote transcription service. |

`remote-ai` is the only canonical remote-inference label. These labels
are deliberately descriptive: a package that has one label is not technically prevented from exercising another
capability, because mods run with the Robotopia process's authority.

The launcher must show the package source, package SHA-256, arbitrary-code warning, and the aggregate capabilities of
the selected package and required dependencies before install or update. A capability is not consent by itself.

## Peer address exposure in player-hosted sessions

Status: recorded decision, not a shipped behavior. TopiaForge ships no live transport; the
[multiplayer hosting feasibility gate](MultiplayerHostingFeasibility.md) is open. This section exists so that when a
transport is designed, address exposure is a decision someone made on purpose rather than a side effect of whichever
connection happened to be cheaper.

**The fact.** A direct peer-to-peer connection reveals each participant's IP address to the participants it connects
to. In a player-hosted session that means the host's address is visible to every participant, and every participant's
address is visible to the host. In a mesh it means every participant sees every other. An IP address is coarse
location data and a durable handle on a person's home connection.

**Why it matters here.** Address exposure is the standard griefing and denial-of-service vector in player-hosted
games: lose a match, look up the host, take their connection offline. It is why platform-provided relays such as Steam
Datagram Relay exist at all — their primary product is address hiding, not latency. TopiaForge cannot use those
relays, because each one requires a game-identity relationship with Robotopia that we do not have.

**The tension, stated plainly.** Relayed traffic is metered and direct traffic is free, so any cost model prefers
direct connections. Direct connections are precisely the ones that expose player addresses. **The cheap axis and the
safe axis are the same axis, pointing in opposite directions.** A transport that quietly optimises for cost is
quietly optimising against player safety. Refusing to notice that is how it ships by default.

### Decision

1. **Direct connect is never a silent default.** A player must be told, before joining or hosting, that a direct
   session reveals their address to the other participants. Consent to play is not consent to be addressable.
2. **Address hiding must remain purchasable at runtime.** The transport must be able to force a relayed path for a
   participant who declines address exposure, and the operating cost of honouring that choice is accepted rather than
   engineered away. A safety control that is disabled when it gets expensive is not a control.
3. **Observed addresses never reach mod code.** `docs/Multiplayer.md` already establishes that process-local identity
   never becomes network identity. The inverse holds with equal force: peer addresses, candidate addresses, and any
   transport-observed endpoint are never exposed to mods, never placed in `MultiplayerSessionSnapshot`, and never
   written to logs, diagnostic bundles, or crash reports. TopiaForge mods are trusted in-process code, so anything a
   mod can read is effectively public.
4. **Disclosure is a capability question, and the current label does not cover it.** `network` means "opens outbound
   network connections". That does not tell a player that other *players* will learn their address. A distinct
   canonical label is required before any live provider ships. Adding one is a coordinated change across
   `schemas/topiaforge.mod.schema.json`, `schemas/topiaforge.mod.v5.schema.json`,
   `src/TopiaForge.ModManager.Core/ManifestValidator.cs`, and
   `packages/launcher_domain/lib/src/models/manifest_contract_constants.dart`, and publication validation treats
   unknown labels as findings. **It is deliberately not being made now**, because shipping a multiplayer capability
   label while the gate is open would advertise a capability that does not exist. It is a prerequisite of closing the
   gate, not of this document.
5. **This is a release blocker in the same sense as the rest of this document.** Player-facing wording about address
   exposure needs the same privacy/legal approval as the remote-AI and microphone text below. Engineering may not
   write the final player-facing sentence.

Transport vendor analysis, including why the address-hiding relays are unavailable to us, is maintainer-internal:
`docs/internal/MultiplayerTransportOptions.md`.


## First-party remote services

RobotKit contains optional integrations with Robotopia's RoboAPI backend. The built-in origin is
`https://api.tomatocake.dev/v1`; a development override is accepted only when it is an absolute HTTPS origin without
credentials, query, or fragment. Redirects are disabled.

**What happens with the features off.** The launcher and loader probe provider availability on every mod load and
scene change so the Diagnostics surface can report why a capability is unavailable. For RobotKit that probe tests
whether the credential file exists and enumerates microphone device names. It does **not** read, parse, or retain the
token value, and it does not open an audio stream — parsing happens only on the request path, which is behind the
consumer opt-in. With every consumer feature off, no request is sent, no token value is loaded, and no audio is
captured.

| Feature | Data sent | Destination | Authentication | Activation and fallback |
| --- | --- | --- | --- | --- |
| Structured robot-brain query | Mod-authored prompt, structured field descriptions, current facts, and a usage label | `/agent/check3` | Bearer value read from the player's bounded `robo_token.json`, plus a random per-session identifier | First-party live-brain features default off. When explicitly enabled, each request is bounded, cancellable, and time-limited. Missing token, offline, timeout, rejection, or malformed output returns an unavailable result; deterministic gameplay must continue. |
| Multi-turn robot conversation | The current player line, a compact transcript of earlier turns, mod-authored system framing, structured decision options, and current facts | `/agent/check3` | Same as above | First-party conversation features default off. Consumers must treat model text as untrusted presentation and use only validated, closed-set decisions for game state. Failure falls back to deterministic behavior. |
| Push-to-talk transcription | Gzip-compressed 16 kHz mono PCM audio, capped at 2 MiB after compression | `/agent/stt` | Same as above | First-party voice input defaults off. Capture begins only after the player enables the feature and performs the documented push-to-talk action. Cancel, missing microphone/token, offline, timeout, or rejection produces no transcript and falls back to typed input. |

Opposite Day and other prompt consumers can register the shared global robot directive through TopiaForge Prompts.
When such a consumer is enabled, RobotKit adds that fixed mod-authored text to an already-enabled structured brain or
conversation request. This creates no additional request, token read, microphone access, endpoint, or logging. The
direct consumer declares `remote-ai` and `prompt-overrides`; the launcher still presents the aggregate capabilities of
it and its dependencies.

The client limits token-file reads to 32 KiB, brain responses to 256 KiB, transcription responses to 64 KiB, and
transcription request bodies to 2 MiB. It does not follow HTTP redirects. Authentication tokens and session headers
must never be written to logs, diagnostic bundles, manifests, lock files, or release metadata.

The remote backend's retention, training use, geographic processing, account linkage, rate limits, abuse handling,
and monetary-cost policy have not been approved in this repository. Do not infer that data is unretained or unused
for training. Public release remains blocked until the backend owner and privacy/legal owner provide accurate text,
authorize these mod-layer calls, and decide whether a separate first-use consent surface is required.

## Required package declarations

A package must declare every capability its behavior or bundled dependencies can exercise. For example, a mod that
uses RobotKit conversations declares `network`, `remote-ai`, and `player-token`; voice input additionally declares
`microphone` and `speech-to-text`. RobotKit itself declares all five because it implements the shared transports.
Consumers should still repeat the capabilities they expose to players so the direct behavioral surface is clear.

Publication validation treats unknown or deprecated capability labels as findings. Official publication has a
zero-finding bar, while ordinary local validation may retain non-publishing compatibility warnings.

## Acceptance matrix

Before enabling any first-party remote feature for public release, retain evidence for:

- signed-out and missing-token behavior;
- microphone permission denied, no-device, device-loss, and cancel behavior;
- offline, DNS failure, TLS failure, redirect, timeout, HTTP 401, HTTP 429, server error, and oversized response;
- mod unload and scene transition while capture or a request is active;
- diagnostic and log scans proving token, authorization, session, transcript, and secret-bearing URL redaction;
- player-facing disclosure, keyboard-only operation, screen-reader labels, and a persistent off switch; and
- approved retention, training, cost, support, abuse, and deletion language supplied by the responsible owners.

Until those checks and approvals are complete, remote AI and microphone/STT remain opt-in and off by default, and
the initial release recommendation remains **NO-SHIP**.
