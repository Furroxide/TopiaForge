part of '../multiplayer.dart';

/// SHA-256 (FIPS 180-4) in plain Dart, so the domain can derive server ids,
/// package digests and descriptor pins without a cryptography dependency.
///
/// The domain already computes its own package-set digest the same way
/// (`packageSetDigestOfCanonical`). This one is checked against the NIST
/// vectors and, in `launcher_data`'s tests, against `package:crypto` across
/// every padding boundary. It targets the Dart VM, where integers are 64-bit.
abstract final class PureSha256 {
  static const digestBytes = 32;
  static const _mask = 0xFFFFFFFF;

  static const _roundConstants = <int>[
    0x428a2f98, 0x71374491, 0xb5c0fbcf, 0xe9b5dba5, 0x3956c25b, 0x59f111f1, //
    0x923f82a4, 0xab1c5ed5, 0xd807aa98, 0x12835b01, 0x243185be, 0x550c7dc3,
    0x72be5d74, 0x80deb1fe, 0x9bdc06a7, 0xc19bf174, 0xe49b69c1, 0xefbe4786,
    0x0fc19dc6, 0x240ca1cc, 0x2de92c6f, 0x4a7484aa, 0x5cb0a9dc, 0x76f988da,
    0x983e5152, 0xa831c66d, 0xb00327c8, 0xbf597fc7, 0xc6e00bf3, 0xd5a79147,
    0x06ca6351, 0x14292967, 0x27b70a85, 0x2e1b2138, 0x4d2c6dfc, 0x53380d13,
    0x650a7354, 0x766a0abb, 0x81c2c92e, 0x92722c85, 0xa2bfe8a1, 0xa81a664b,
    0xc24b8b70, 0xc76c51a3, 0xd192e819, 0xd6990624, 0xf40e3585, 0x106aa070,
    0x19a4c116, 0x1e376c08, 0x2748774c, 0x34b0bcb5, 0x391c0cb3, 0x4ed8aa4a,
    0x5b9cca4f, 0x682e6ff3, 0x748f82ee, 0x78a5636f, 0x84c87814, 0x8cc70208,
    0x90befffa, 0xa4506ceb, 0xbef9a3f7, 0xc67178f2,
  ];

  /// The 32-byte digest of [data].
  static Uint8List hash(List<int> data) {
    final length = data.length;
    final paddedLength = (length + 9 + 63) ~/ 64 * 64;
    final message = Uint8List(paddedLength);
    for (var index = 0; index < length; index++) {
      final value = data[index];
      if (value < 0 || value > 0xFF) {
        throw ArgumentError.value(value, 'data', 'Each value must be a byte.');
      }
      message[index] = value;
    }
    message[length] = 0x80;
    final view = ByteData.sublistView(message);
    final bitLength = length * 8;
    view.setUint32(paddedLength - 8, bitLength ~/ 0x100000000);
    view.setUint32(paddedLength - 4, bitLength & _mask);

    final state = <int>[
      0x6a09e667, 0xbb67ae85, 0x3c6ef372, 0xa54ff53a, //
      0x510e527f, 0x9b05688c, 0x1f83d9ab, 0x5be0cd19,
    ];
    final schedule = Uint32List(64);
    for (var chunk = 0; chunk < paddedLength; chunk += 64) {
      for (var index = 0; index < 16; index++) {
        schedule[index] = view.getUint32(chunk + index * 4);
      }
      for (var index = 16; index < 64; index++) {
        final early = schedule[index - 15];
        final late = schedule[index - 2];
        final sigma0 = _rotate(early, 7) ^ _rotate(early, 18) ^ (early >> 3);
        final sigma1 = _rotate(late, 17) ^ _rotate(late, 19) ^ (late >> 10);
        schedule[index] =
            (schedule[index - 16] + sigma0 + schedule[index - 7] + sigma1) &
            _mask;
      }
      var a = state[0], b = state[1], c = state[2], d = state[3];
      var e = state[4], f = state[5], g = state[6], h = state[7];
      for (var index = 0; index < 64; index++) {
        final sum1 = _rotate(e, 6) ^ _rotate(e, 11) ^ _rotate(e, 25);
        final choice = (e & f) ^ ((~e & _mask) & g);
        final first =
            (h + sum1 + choice + _roundConstants[index] + schedule[index]) &
            _mask;
        final sum0 = _rotate(a, 2) ^ _rotate(a, 13) ^ _rotate(a, 22);
        final majority = (a & b) ^ (a & c) ^ (b & c);
        final second = (sum0 + majority) & _mask;
        h = g;
        g = f;
        f = e;
        e = (d + first) & _mask;
        d = c;
        c = b;
        b = a;
        a = (first + second) & _mask;
      }
      state[0] = (state[0] + a) & _mask;
      state[1] = (state[1] + b) & _mask;
      state[2] = (state[2] + c) & _mask;
      state[3] = (state[3] + d) & _mask;
      state[4] = (state[4] + e) & _mask;
      state[5] = (state[5] + f) & _mask;
      state[6] = (state[6] + g) & _mask;
      state[7] = (state[7] + h) & _mask;
    }

    final digest = Uint8List(digestBytes);
    final output = ByteData.sublistView(digest);
    for (var index = 0; index < 8; index++) {
      output.setUint32(index * 4, state[index]);
    }
    return digest;
  }

  /// The digest of [data] in lowercase hexadecimal.
  static String hex(List<int> data) => WireText.toHex(hash(data));

  static int _rotate(int value, int count) =>
      ((value >> count) | (value << (32 - count))) & _mask;
}
