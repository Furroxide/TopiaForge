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

TopiaForge is maintained by one person on a best-effort basis, with no guaranteed response time. The intent is to
acknowledge a private report, agree on disclosure timing with the reporter, and publish an advisory once users have
had a reasonable update window. Whether a fix ships, and how quickly, depends on severity and on maintainer capacity;
for a low-severity issue the outcome may be an advisory and a documented workaround rather than a patch. Security
fixes must not be disclosed through a public pull request before coordinated release.

## Supported versions

Before the first stable release, only the current `main` branch is eligible for security fixes. After release, only the
latest stable line is considered, on the same best-effort basis; older lines are not supported unless a published
advisory says otherwise. Backporting to any earlier line is not promised. This is a single-maintainer project and does
not offer long-term support.

Third-party vulnerabilities in BepInEx, UnityDoorstop, HarmonyX, MonoMod, Mono.Cecil, Flutter, Dart, Node packages, or
Unity should also be reported upstream, while Robotopia-specific packaging or integration impact should be reported
here privately.
