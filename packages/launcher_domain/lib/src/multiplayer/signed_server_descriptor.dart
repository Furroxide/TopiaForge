part of '../multiplayer.dart';

/// A server descriptor with the host's signature, exactly as the descriptor
/// endpoint and the control plane serve it: the canonical JSON object
/// `{"descriptor":{...},"signature":"..."}`.
///
/// A parsed envelope is not trusted yet. Only [ServerDescriptorVerifier] turns
/// one into a [VerifiedServerDescriptor], after the signature checks out.
final class SignedServerDescriptorV1 {
  SignedServerDescriptorV1._(this.descriptor, Uint8List signature, this.bytes)
    : signature = signature.asUnmodifiableView(),
      sha256 = PureSha256.hex(bytes);

  /// The largest signed descriptor anyone accepts, in bytes.
  static const maximumBytes = 256 * 1024;

  /// The length of an Ed25519 signature.
  static const signatureBytes = 64;

  /// The 32-byte ASCII label the signature covers before the descriptor, so
  /// a descriptor signature cannot pass as a signature over anything else.
  static final Uint8List signatureContext = Uint8List.fromList(
    ascii.encode('TopiaForge server descriptor v1\n'),
  ).asUnmodifiableView();

  final ServerDescriptorV1 descriptor;
  final Uint8List signature;

  /// The exact canonical bytes that were served, which a join plan pins.
  final Uint8List bytes;

  /// The lowercase hexadecimal SHA-256 of [bytes].
  final String sha256;

  /// What the signature covers: [signatureContext] followed by the
  /// descriptor's canonical bytes.
  Uint8List get signedMessage {
    final canonical = descriptor.canonicalBytes;
    return Uint8List(signatureContext.length + canonical.length)
      ..setRange(0, signatureContext.length, signatureContext)
      ..setRange(
        signatureContext.length,
        signatureContext.length + canonical.length,
        canonical,
      );
  }

  /// Reads served bytes: canonical JSON within [maximumBytes], exactly a
  /// descriptor and a signature, and every descriptor member valid and
  /// consistent. The signature itself is not checked here. Throws
  /// [FormatException].
  factory SignedServerDescriptorV1.parse(List<int> bytes) {
    final document = CanonicalJson.decode(bytes, maximumBytes: maximumBytes);
    if (document is! Map<String, Object?> ||
        !_hasExactly(document, const {'descriptor', 'signature'})) {
      throw const FormatException(
        'A signed descriptor has exactly a descriptor and a signature.',
      );
    }
    final signature = WireText.tryParseBase64Url(
      _requireString(document, 'signature'),
      signatureBytes,
    );
    if (signature == null) {
      throw const FormatException('The signature is malformed.');
    }
    final descriptor = ServerDescriptorV1.fromJson(document['descriptor']);
    return SignedServerDescriptorV1._(
      descriptor,
      signature,
      Uint8List.fromList(bytes).asUnmodifiableView(),
    );
  }

  /// Wraps [descriptor] and an existing [signature] into the served envelope.
  /// The signature is not checked; verify the result before trusting it.
  factory SignedServerDescriptorV1.assemble(
    ServerDescriptorV1 descriptor,
    List<int> signature,
  ) {
    if (signature.length != signatureBytes) {
      throw ArgumentError.value(
        signature.length,
        'signature',
        'An Ed25519 signature is 64 bytes.',
      );
    }
    final bytes = CanonicalJson.encode({
      'descriptor': descriptor.toJson(),
      'signature': WireText.toBase64Url(signature),
    });
    if (bytes.length > maximumBytes) {
      throw ArgumentError.value(
        bytes.length,
        'descriptor',
        'The signed descriptor is larger than 256 KiB.',
      );
    }
    return SignedServerDescriptorV1._(
      descriptor,
      Uint8List.fromList(signature),
      bytes.asUnmodifiableView(),
    );
  }
}

/// Checks the Ed25519 signature on a server descriptor. The data layer
/// implements it, so the domain itself stays free of cryptography.
abstract interface class ServerDescriptorSignatureVerifier {
  /// Whether [envelope]'s signature is a valid Ed25519 signature by the
  /// descriptor's own signing key over [SignedServerDescriptorV1.signedMessage].
  Future<bool> verifySignature(SignedServerDescriptorV1 envelope);
}

/// Why a served descriptor was refused.
enum ServerDescriptorRefusal {
  /// Not canonical JSON within the size bound, or a member is missing,
  /// unknown, invalid or inconsistent.
  malformed,

  /// A valid descriptor, but for another server than the one expected.
  otherServer,

  /// The signature does not verify under the descriptor's signing key.
  badSignature,
}

/// The outcome of [ServerDescriptorVerifier.verify].
sealed class ServerDescriptorVerification {
  const ServerDescriptorVerification();
}

/// A descriptor that may pin a transport key.
final class ServerDescriptorAccepted extends ServerDescriptorVerification {
  const ServerDescriptorAccepted(this.descriptor);

  final VerifiedServerDescriptor descriptor;
}

/// A descriptor that must not be trusted. [message] names no address.
final class ServerDescriptorRefused extends ServerDescriptorVerification {
  const ServerDescriptorRefused(
    this.refusal,
    this.message, {
    this.servedServerId,
  });

  final ServerDescriptorRefusal refusal;
  final String message;

  /// For [ServerDescriptorRefusal.otherServer]: the server the descriptor
  /// names. Its signature was not checked, so it proves nothing yet. A typed
  /// address reports it as an identity change, and only a join that then
  /// trusts it can verify it.
  final String? servedServerId;
}

/// A descriptor whose signature verified under its own signing key and, when
/// one was expected, whose server id is the expected one. Only
/// [ServerDescriptorVerifier] creates these, so holding one is proof of the
/// checks.
final class VerifiedServerDescriptor {
  VerifiedServerDescriptor._(this.envelope);

  final SignedServerDescriptorV1 envelope;

  ServerDescriptorV1 get descriptor => envelope.descriptor;

  String get serverId => envelope.descriptor.serverId;

  /// The SHA-256 of the exact served bytes, which a join plan pins.
  String get descriptorSha256 => envelope.sha256;

  String get packageDigest => envelope.descriptor.packageDigest;

  /// The X25519 key the transport handshake must present.
  Uint8List get transportKey => envelope.descriptor.transportKey;
}

/// Checks served descriptors before anything in them is trusted.
final class ServerDescriptorVerifier {
  const ServerDescriptorVerifier(this._signatures);

  final ServerDescriptorSignatureVerifier _signatures;

  /// Accepts [bytes] only when they are canonical JSON of at most 256 KiB,
  /// every member is valid, the server id and package digest agree, the
  /// signature verifies under the signing key inside, and, when
  /// [expectedServerId] is given, that key's hash is [expectedServerId].
  ///
  /// Served bytes are hostile input, so nothing they or the signature check
  /// do can make this throw or accept: every failure is a refusal.
  Future<ServerDescriptorVerification> verify(
    List<int> bytes, {
    String? expectedServerId,
  }) async {
    final SignedServerDescriptorV1 envelope;
    try {
      envelope = SignedServerDescriptorV1.parse(bytes);
    } on FormatException catch (error) {
      return ServerDescriptorRefused(
        ServerDescriptorRefusal.malformed,
        error.message,
      );
    } on Object {
      return const ServerDescriptorRefused(
        ServerDescriptorRefusal.malformed,
        'The descriptor could not be read.',
      );
    }
    if (expectedServerId != null &&
        envelope.descriptor.serverId != expectedServerId) {
      return ServerDescriptorRefused(
        ServerDescriptorRefusal.otherServer,
        'The descriptor belongs to another server.',
        servedServerId: envelope.descriptor.serverId,
      );
    }
    final bool valid;
    try {
      valid = await _signatures.verifySignature(envelope);
    } on Object {
      return const ServerDescriptorRefused(
        ServerDescriptorRefusal.badSignature,
        'The descriptor signature could not be checked.',
      );
    }
    if (!valid) {
      return const ServerDescriptorRefused(
        ServerDescriptorRefusal.badSignature,
        'The descriptor signature does not verify.',
      );
    }
    return ServerDescriptorAccepted(VerifiedServerDescriptor._(envelope));
  }
}
