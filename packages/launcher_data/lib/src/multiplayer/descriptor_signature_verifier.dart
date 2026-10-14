import 'package:cryptography/cryptography.dart';
import 'package:launcher_domain/launcher_domain.dart';

/// Checks server descriptor signatures with Ed25519 (RFC 8032): the signature
/// must verify under the descriptor's own signing key over the 32-byte ASCII
/// label `TopiaForge server descriptor v1\n` followed by the descriptor's
/// canonical bytes ([SignedServerDescriptorV1.signedMessage]).
///
/// Use it through the domain's [ServerDescriptorVerifier], which checks the
/// document, the server id and the package digest first:
/// `const ServerDescriptorVerifier(Ed25519DescriptorSignatureVerifier())`.
///
/// `package:cryptography` already refuses non-canonical point encodings and
/// scalars of the group order or more. This also refuses signing keys of
/// small order, as the game's verifier does: anyone can forge a signature
/// under such a key, so it cannot stand for a server.
final class Ed25519DescriptorSignatureVerifier
    implements ServerDescriptorSignatureVerifier {
  const Ed25519DescriptorSignatureVerifier();

  /// The y-coordinates, little-endian, of the eight edwards25519 points whose
  /// order divides 8: the identity, the point of order 2, the two of order 4
  /// and the four of order 8. Each y stands for both signs of x.
  static const _smallOrderY = {
    '0000000000000000000000000000000000000000000000000000000000000000',
    '0100000000000000000000000000000000000000000000000000000000000000',
    '26e8958fc2b227b045c3f489f2ef98f0d5dfac05d3c63339b13802886d53fc05',
    'c7176a703d4dd84fba3c0b760d10670f2a2053fa2c39ccc64ec7fd7792ac037a',
    'ecffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff7f',
  };

  /// Whether [publicKey] encodes a point of small order.
  static bool hasSmallOrder(List<int> publicKey) {
    if (publicKey.length != ServerDescriptorV1.keyBytes) return false;
    final y = [...publicKey];
    y[31] &= 0x7F;
    return _smallOrderY.contains(WireText.toHex(y));
  }

  @override
  Future<bool> verifySignature(SignedServerDescriptorV1 envelope) async {
    final signingKey = envelope.descriptor.signingKey;
    if (hasSmallOrder(signingKey)) return false;
    return Ed25519().verify(
      envelope.signedMessage,
      signature: Signature(
        envelope.signature,
        publicKey: SimplePublicKey(signingKey, type: KeyPairType.ed25519),
      ),
    );
  }
}
