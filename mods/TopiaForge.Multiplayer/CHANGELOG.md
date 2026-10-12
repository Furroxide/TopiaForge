# Changelog

## 0.1.0-rc.1

- Added the stable Unity-free multiplayer authoring contract and generated registration, codecs, snapshots, and prediction support.
- Added the solo provider, which runs the multiplayer session engine in single-player play while live transport remains unsupported.
- Added the live transport's cryptography (X25519, Ed25519, ChaCha20-Poly1305, HKDF and the Noise XX handshake) over a vendored BouncyCastle subset, with its notices in `THIRD_PARTY_NOTICES.md`. Nothing uses it until live transport ships.
- Added deterministic admission metadata, ownership validation, rollback/replay, and late-join state handling.
