# Security Policy

TopiaForge mods execute native-trust C# code inside the game process. A valid package hash proves integrity, not safety;
install packages only from authors you trust.

Sensitive first-party network, player-token, remote-AI, microphone, and speech-to-text behavior is inventoried in
[`docs/PrivacyAndCapabilities.md`](docs/PrivacyAndCapabilities.md). Do not include tokens, authorization/session
headers, transcripts, recordings, or secret-bearing URLs in a vulnerability report unless the private reporting
channel specifically requests a minimal encrypted sample.

## Reporting a vulnerability

Use GitHub's private vulnerability reporting for this repository when available; repository administrator
`@furroxide` is the interim intake owner. If private reporting is unavailable, contact that owner privately through
GitHub and ask for a confidential reporting channel. Do not open a public issue for an unpatched vulnerability or
include secrets, game-account data, private paths, or proprietary game assemblies.

Include the affected component/version, impact, reproduction steps, proof-of-concept files, and any suggested
mitigation. Reports involving archive traversal, signature/hash bypass, unsafe process launch, registry compromise,
credential exposure, loader privilege boundaries, or remote UGC sessions are especially useful.

Maintainers should acknowledge a private report, agree on disclosure timing, prepare fixes for supported release lines,
and publish an advisory after users have a reasonable update window. Security fixes must not be disclosed through a
public pull request before coordinated release.

## Supported versions

Before the first stable release, only the current `main` branch is eligible for security fixes. After release, the
latest stable line is supported; older lines are supported only when a published advisory says otherwise.

Third-party vulnerabilities in BepInEx, UnityDoorstop, HarmonyX, MonoMod, Mono.Cecil, Flutter, Dart, Node packages, or
Unity should also be reported upstream, while Robotopia-specific packaging or integration impact should be reported
here privately.
