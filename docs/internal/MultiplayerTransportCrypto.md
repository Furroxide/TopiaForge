# Multiplayer transport cryptography — design record

Status: Implementing (WS2d of the live-multiplayer plan) · Date: 2026-10-12 · Owner: multiplayer

This records the cryptography the live transport (WS2e) and LAN and direct sessions (WS2f) build on. Nothing uses it
yet: the provider still hosts only solo sessions. The code lives in `mods/TopiaForge.Multiplayer/Transport/Crypto/`,
inside the provider only, and every type is internal.

## Primitives

| Purpose | Primitive | Implementation |
| --- | --- | --- |
| Key agreement | X25519 (RFC 7748) | Vendored BouncyCastle |
| Signatures | Ed25519, pure mode (RFC 8032) | Vendored BouncyCastle |
| Authenticated encryption | ChaCha20-Poly1305 (RFC 8439) | Vendored BouncyCastle |
| Hash and key derivation | SHA-256, HKDF with HMAC-SHA256 (RFC 5869) | .NET class library |
| Randomness | `RandomNumberGenerator` | .NET class library |

The game's Mono class library has SHA-256, HMAC and a working random number generator, but no ChaCha20-Poly1305,
X25519 or Ed25519 (`MultiplayerMonoSpike.md`, section 4). Those three come from BouncyCastle 2.7.0, compiled into the
provider as source:

- A renamed internal namespace keeps it from colliding with another BouncyCastle assembly in the game, a conflict
  Mono has no binding redirects to settle.
- The provider targets netstandard2.1, which BouncyCastle never builds for. Its `NETSTANDARD2_1_OR_GREATER` paths
  call APIs only newer .NET has. The vendored copy renames that symbol away, so it compiles the same code as
  BouncyCastle's own netstandard2.0 build, the one the Mono spike measured at about 200 MB/s for ChaCha20-Poly1305.
- Every key comes from `CryptoRandom`, a locked `RandomNumberGenerator`. BouncyCastle's `SecureRandom` overloads are
  removed rather than shimmed.

`THIRD_PARTY_NOTICES.md` records provenance and changes. The provider's own notices file, which ships in its package,
lists every file with its upstream blob hash.

## Handshake

`NoiseHandshakeXX` implements `Noise_XX_25519_ChaChaPoly_SHA256`:

```text
-> e
<- e, ee, s, es
-> s, se
```

- Each side proves its static key and learns the other's. Both statics travel encrypted. The responder's is protected
  by `ee`, so it is hidden from passive observers. The initiator's is protected by `ee` and `es`, so only the
  responder learns it.
- The prologue binds whatever both sides must agree on before they speak; WS2e fixes its contents.
- `HandshakeHash` binds the whole transcript. WS2f's password proof, `HMAC(PBKDF2(password, hostStaticPub),
  handshakeHash)`, uses it as a channel binding, so a proof cannot be replayed into another handshake.
- A message that fails to authenticate ends the handshake for good, as does a low-order peer key (RFC 7748
  section 6.1). The responder catches a low-order ephemeral key when it mixes its first Diffie-Hellman result.
- Pinning a host's static key against descriptors, beacons and links, and trust on first use otherwise, belong to
  WS2f.

## Transport keys

`Split` yields one `CipherState` per direction.

- **Ordered messages** use the implicit nonce, which advances only when a message authenticates.
- **Datagrams** carry an explicit 64-bit counter. A `ReplayWindow` of 2,048 counters is checked before decryption and
  updated only after the datagram authenticates, so a forgery never moves it. Counters older than the window, and
  repeats, are dropped.
- **Nonce limits.** The nonce `2^64 − 1` is reserved. Nothing is sealed with it, and `Rekey` derives the next key from
  it as Noise specifies, keeping the implicit nonce running. The replay window still accepts that last counter
  correctly; its walk stops on the counter rather than stepping past the top of the range.
- **Thread affinity.** A cipher state is not thread-safe. WS2f's network thread owns both directions of a connection.

## Key hygiene

Secret material is zeroed when its owner is done with it. Managed memory makes that best effort, since the collector
may have copied an array before it was cleared, but it is consistent:

- Buffers this code allocates for secrets are cleared once used: Diffie-Hellman results, HKDF's extracted key and
  output blocks, chaining keys, and the outputs of `Split` and `Rekey`.
- `ChaCha20Poly1305` holds three copies of its key: its own, the vendored cipher's, and the cipher's engine state.
  Disposing it zeroes all three, with the keystream and the Poly1305 one-time key derived from them. The vendored
  `KeyParameter`, `Poly1305` and `ChaCha20Poly1305` each gain an internal `Wipe` for this.
- A `CipherState` zeroes the key it replaces on `InitializeKey` or `Rekey`, and its key when disposed. A disposed
  state refuses all work rather than falling back to the keyless pass-through Noise defines.
- Disposing a `NoiseHandshakeXX` zeroes its static and ephemeral keys, chaining key and handshake cipher state. The
  `TransportKeys` it splits off belong to the caller, who disposes them when the connection ends.

## Verification

`TransportCryptoVectorTests` runs the published vectors:

- RFC 7748: both section 5.2 multiplications, 1 and 1,000 iterations, the section 6.1 exchange, and low-order
  rejection;
- RFC 8032: section 7.1 tests 1, 2, 3 and SHA(abc);
- RFC 8439: section 2.8.2;
- RFC 5869: SHA-256 test cases 1 and 3.

Every vector was also checked against OpenSSL 3.5.

`NoiseHandshakeTests` checks the handshake against two references, both byte for byte across the handshake messages,
handshake hash and transport ciphertexts:

- The `Noise_XX_25519_ChaChaPoly_SHA256` case of the Cacophony test vectors: `vectors/cacophony.txt` at
  haskell-cryptography/cacophony commit `8ee9d41e34a1a596cfa3ab12aa4069ff87dc1247`, released under the Unlicense.
  Only that case is copied into the test.
- A fixed-key transcript from an independent Noise implementation written over OpenSSL primitives.

The same tests cover tampering, prologue mismatch, turn order, low-order keys, cipher-state rules, rekeying and the
replay window, including the top of its counter range. They also read every key copy back after disposal and require
zeros.
