# Privacy and Capability Disclosure

Status: engineering disclosure for the initial-release candidate. This document does **not** replace an approved
privacy notice, backend authorization, or platform microphone-consent text. Those approvals remain release blockers.

Robotopia mods are trusted in-process C# code. Manifest capabilities explain potentially sensitive behavior to a
player; they do not sandbox, mediate, or grant that behavior. Install only packages whose author and source you trust.

## Canonical capabilities

| Capability | Meaning |
| --- | --- |
| `network` | Opens outbound network connections. |
| `remote-ai` | Sends inputs to a remote inference service and consumes its response. |
| `player-token` | Reads the player's Robotopia authentication token for an explicitly enabled integration. |
| `microphone` | Captures audio from a local microphone after an explicit player action. |
| `speech-to-text` | Sends captured audio to a remote transcription service. |

`ai` is a deprecated compatibility alias for `remote-ai`. New manifests must use the canonical labels. These labels
are deliberately descriptive: a package that has one label is not technically prevented from exercising another
capability, because mods run with the game process's authority.

The launcher must show the package source, package SHA-256, arbitrary-code warning, and the aggregate capabilities of
the selected package and required dependencies before install or update. A capability is not consent by itself.

## First-party remote services

RobotKit contains optional integrations with the game's RoboAPI backend. The built-in origin is
`https://api.tomatocake.dev/v1`; a development override is accepted only when it is an absolute HTTPS origin without
credentials, query, or fragment. Redirects are disabled.

| Feature | Data sent | Destination | Authentication | Activation and fallback |
| --- | --- | --- | --- | --- |
| Structured robot-brain query | Mod-authored prompt, structured field descriptions, current facts, and a usage label | `/agent/check3` | Bearer value read from the player's bounded `robo_token.json`, plus a random per-session identifier | First-party live-brain features default off. When explicitly enabled, each request is bounded, cancellable, and time-limited. Missing token, offline, timeout, rejection, or malformed output returns an unavailable result; deterministic gameplay must continue. |
| Multi-turn robot conversation | The current player line, a compact transcript of earlier turns, mod-authored system framing, structured decision options, and current facts | `/agent/check3` | Same as above | First-party conversation features default off. Consumers must treat model text as untrusted presentation and use only validated, closed-set decisions for game state. Failure falls back to deterministic behavior. |
| Push-to-talk transcription | Gzip-compressed 16 kHz mono PCM audio, capped at 2 MiB after compression | `/agent/stt` | Same as above | First-party voice input defaults off. Capture begins only after the player enables the feature and performs the documented push-to-talk action. Cancel, missing microphone/token, offline, timeout, or rejection produces no transcript and falls back to typed input. |

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
zero-finding bar, while ordinary local validation can retain compatibility warnings for legacy packages.

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
