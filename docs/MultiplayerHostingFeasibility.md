---
title: Multiplayer hosting feasibility gate
description: Distinguish development listen-server transport from public-release and dedicated-hosting qualification.
---

# Multiplayer hosting feasibility gate

TopiaForge implements player-hosted listen sessions for development: an interactive Robotopia client hosts the
authoritative world while its player participates. Live sessions remain behind the explicit
`TOPIAFORGE_LIVE_MULTIPLAYER=1` switch. This implementation is not an approved public release, a supported dedicated
Robotopia server, or permission to redistribute game files.

## Current implementation evidence

The provider contains authenticated UDP transport, LAN discovery, direct hosting/joining, Internet signaling and
relay paths, reconnect handling, and optional voice. The in-game session screens enforce the approved privacy
prompts; voice remains off until the player enables it. See [Multiplayer](Multiplayer.md) and
[privacy and capability disclosure](PrivacyAndCapabilities.md).

Executable source fixtures now exist rather than only a transport design:

| Area | Source evidence | Limit |
| --- | --- | --- |
| Session engine, authentication and join/leave | `LiveSessionTests`, `TransportDeliveryTests`, `EngineRoleTests` | Deterministic transport fixtures do not qualify a native Robotopia build. |
| Relay-only privacy and service failures | `NatPrivacyTests`, `InternetFailureTests`, control-plane privacy/signaling tests | Service configuration and operator practices need their own evidence. |
| Local socket sessions | `DeveloperPeerSocketTests` | Loopback tests do not prove Internet reachability or multi-machine play. |
| Voice opt-in and lifecycle | `VoiceProviderTests`, `VoiceChatTests` | Managed fixtures do not prove native microphone consent, devices or audio behavior. |

The provider sources are under `mods/TopiaForge.Multiplayer`; the runtime session and voice adapters are under
`src/TopiaForge.ModManager/Multiplayer`. These references describe test coverage in source, not a claim that every
fixture was rerun for a particular candidate.

## Public-release qualification still required

The release blockers in `docs/LaunchBlockers.md` remain authoritative. Current-build metadata compatibility and managed
tests do not replace exact-candidate native acceptance, the full multiplayer rehearsal, approved disclosures, rights
and licensing decisions, or service qualification. Live enablement and service deployment are separate decisions;
the presence of transport code does not enable either by default.

The hosting model uses the player's installed interactive game. TopiaForge release archives must not contain
Robotopia executables, managed assemblies, data files or derived server builds. The unresolved rights gate is not
closed by technical feasibility or by the game's map-sharing features.

## Dedicated and headless hosting remain unsupported

There is no supported dedicated Robotopia executable or independently qualified headless bootstrap in this
repository. The SDK's logical dedicated-server role and Unity's
[Dedicated Server sub-target](https://docs.unity3d.com/6000.0/Documentation/Manual/dedicated-server-introduction.html)
do not establish that a compiled third-party game can run without its interactive player or be distributed that way.

A separate dedicated-hosting decision still requires reproducible evidence for the exact pinned game build:

1. **Bootstrap:** predictable startup, failure reporting and shutdown without a player, graphics, audio or UI.
2. **Simulation:** world, scene, physics and gameplay progress correctly without presentation dependencies.
3. **Integration:** authenticated admission, bounded commands, reconnect snapshots and cancellation work in that
   headless process without exposing native transport objects to mods.
4. **Distribution and licensing:** a verified rights basis for the deployment model and its actual file inventory.
5. **Operations:** supported-platform acceptance, compatibility updates, resource limits, abuse handling, recovery
   and rollout/rollback evidence.

Listen-server tests do not satisfy those headless requirements. No dedicated-server download or automatic execution
of a host-supplied game package is authorized by this implementation.
