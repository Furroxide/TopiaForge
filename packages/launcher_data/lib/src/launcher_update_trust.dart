import 'dart:convert';
import 'dart:typed_data';

import 'package:crypto/crypto.dart';
import 'package:cryptography/cryptography.dart';

const launcherUpdateSignatureAlgorithm = 'Ed25519';
const launcherUpdatePayloadFormatVersion = 1;
const launcherUpdateSignatureFormatVersion = 1;

final class LauncherUpdateTrustedKey {
  LauncherUpdateTrustedKey({required this.id, required List<int> publicKey})
    : publicKey = Uint8List.fromList(publicKey) {
    if (this.publicKey.length != 32 || id != launcherUpdateKeyId(publicKey)) {
      throw const FormatException('Launcher update public key is invalid.');
    }
  }

  factory LauncherUpdateTrustedKey.fromJson(Map<String, Object?> json) {
    const fields = {'id', 'algorithm', 'publicKey'};
    final keys = json.keys.toSet();
    if (keys.difference(fields).isNotEmpty ||
        fields.difference(keys).isNotEmpty ||
        json['algorithm'] != launcherUpdateSignatureAlgorithm) {
      throw const FormatException('Launcher update public key is invalid.');
    }
    final encoded = json['publicKey'] as String? ?? '';
    return LauncherUpdateTrustedKey(
      id: json['id'] as String? ?? '',
      publicKey: _decodeBase64(encoded, label: 'publicKey', expectedBytes: 32),
    );
  }

  final String id;
  final Uint8List publicKey;

  Map<String, Object?> toJson() => {
    'id': id,
    'algorithm': launcherUpdateSignatureAlgorithm,
    'publicKey': base64Encode(publicKey),
  };
}

final class LauncherUpdateTrustStore {
  LauncherUpdateTrustStore(Iterable<LauncherUpdateTrustedKey> keys)
    : _keys = Map.unmodifiable({for (final key in keys) key.id: key}) {
    if (_keys.isEmpty || _keys.length != keys.length) {
      throw const FormatException(
        'Launcher update trust store must contain unique keys.',
      );
    }
  }

  factory LauncherUpdateTrustStore.fromJson(Map<String, Object?> json) {
    const fields = {r'$schema', 'formatVersion', 'keys'};
    const schema =
        'https://raw.githubusercontent.com/furroxide/TopiaForge/main/'
        'schemas/topiaforge.update-keys.schema.json';
    final keys = json.keys.toSet();
    if (keys.difference(fields).isNotEmpty ||
        keys.difference({r'$schema'}).length != 2 ||
        (json.containsKey(r'$schema') && json[r'$schema'] != schema) ||
        json['formatVersion'] != 1 ||
        json['keys'] is! List) {
      throw const FormatException(
        'Launcher update trust store format is invalid.',
      );
    }
    final values = json['keys']! as List;
    if (values.isEmpty || values.length > 8) {
      throw const FormatException(
        'Launcher update trust store key count is invalid.',
      );
    }
    return LauncherUpdateTrustStore([
      for (final value in values)
        LauncherUpdateTrustedKey.fromJson(
          Map<String, Object?>.from(value as Map),
        ),
    ]);
  }

  factory LauncherUpdateTrustStore.embedded() =>
      LauncherUpdateTrustStore.fromJson(const {
        'formatVersion': 1,
        'keys': [
          {
            'id': 'ed25519:26229e3d2b54e81c',
            'algorithm': 'Ed25519',
            'publicKey': 'JyrRg0k51FD4FrygRL0WHx78g1tVqTPy/Y28TbC7ccg=',
          },
        ],
      });

  final Map<String, LauncherUpdateTrustedKey> _keys;

  Set<String> get keyIds => Set.unmodifiable(_keys.keys);

  Future<VerifiedLauncherUpdatePayload> verify({
    required List<int> payloadBytes,
    required List<int> signatureBytes,
  }) async {
    if (payloadBytes.isEmpty || payloadBytes.length > 1024 * 1024) {
      throw const FormatException('Launcher update payload size is invalid.');
    }
    if (signatureBytes.isEmpty || signatureBytes.length > 16 * 1024) {
      throw const FormatException('Launcher update signature size is invalid.');
    }
    final signatureJson = _decodeObject(
      signatureBytes,
      label: 'Launcher update signature',
    );
    if (signatureJson['formatVersion'] !=
            launcherUpdateSignatureFormatVersion ||
        signatureJson['algorithm'] != launcherUpdateSignatureAlgorithm ||
        signatureJson.keys.toSet().difference(const {
          'formatVersion',
          'algorithm',
          'keyId',
          'signature',
        }).isNotEmpty ||
        signatureJson.length != 4) {
      throw const FormatException(
        'Launcher update signature format is invalid.',
      );
    }
    final keyId = signatureJson['keyId'] as String? ?? '';
    final key = _keys[keyId];
    if (key == null) {
      throw const FormatException(
        'Launcher update signing key is not trusted.',
      );
    }
    final signature = _decodeBase64(
      signatureJson['signature'] as String? ?? '',
      label: 'signature',
      expectedBytes: 64,
    );
    final verified = await Ed25519().verify(
      payloadBytes,
      signature: Signature(
        signature,
        publicKey: SimplePublicKey(key.publicKey, type: KeyPairType.ed25519),
      ),
    );
    if (!verified) {
      throw const FormatException(
        'Launcher update signature verification failed.',
      );
    }
    final payload = _decodeObject(
      payloadBytes,
      label: 'Launcher update payload',
    );
    if (payload['formatVersion'] != launcherUpdatePayloadFormatVersion) {
      throw const FormatException('Launcher update payload format is invalid.');
    }
    return VerifiedLauncherUpdatePayload(
      keyId: keyId,
      payload: Map.unmodifiable(payload),
      sha256: sha256.convert(payloadBytes).toString(),
    );
  }
}

final class VerifiedLauncherUpdatePayload {
  const VerifiedLauncherUpdatePayload({
    required this.keyId,
    required this.payload,
    required this.sha256,
  });

  final String keyId;
  final Map<String, Object?> payload;
  final String sha256;
}

final class LauncherUpdateKeyMaterial {
  LauncherUpdateKeyMaterial._({
    required List<int> privateSeed,
    required this.publicKey,
  }) : privateSeed = Uint8List.fromList(privateSeed);

  final Uint8List privateSeed;
  final LauncherUpdateTrustedKey publicKey;

  static Future<LauncherUpdateKeyMaterial> generate() async {
    final pair = await Ed25519().newKeyPair();
    final seed = await pair.extractPrivateKeyBytes();
    final public = await pair.extractPublicKey();
    return LauncherUpdateKeyMaterial._(
      privateSeed: seed,
      publicKey: LauncherUpdateTrustedKey(
        id: launcherUpdateKeyId(public.bytes),
        publicKey: public.bytes,
      ),
    );
  }

  static Future<LauncherUpdateKeyMaterial> fromSeed(List<int> seed) async {
    if (seed.length != 32) {
      throw const FormatException(
        'Launcher update private seed must be 32 bytes.',
      );
    }
    final pair = await Ed25519().newKeyPairFromSeed(seed);
    final public = await pair.extractPublicKey();
    return LauncherUpdateKeyMaterial._(
      privateSeed: seed,
      publicKey: LauncherUpdateTrustedKey(
        id: launcherUpdateKeyId(public.bytes),
        publicKey: public.bytes,
      ),
    );
  }

  Future<Uint8List> sign(List<int> payloadBytes) async {
    final pair = await Ed25519().newKeyPairFromSeed(privateSeed);
    final signature = await Ed25519().sign(payloadBytes, keyPair: pair);
    final sidecar = <String, Object?>{
      'formatVersion': launcherUpdateSignatureFormatVersion,
      'algorithm': launcherUpdateSignatureAlgorithm,
      'keyId': publicKey.id,
      'signature': base64Encode(signature.bytes),
    };
    return Uint8List.fromList(
      utf8.encode('${const JsonEncoder.withIndent('  ').convert(sidecar)}\n'),
    );
  }
}

String launcherUpdateKeyId(List<int> publicKey) {
  if (publicKey.length != 32) {
    throw const FormatException('Ed25519 public keys must be 32 bytes.');
  }
  final digest = sha256.convert(publicKey).toString().substring(0, 16);
  return 'ed25519:$digest';
}

Map<String, Object?> _decodeObject(List<int> bytes, {required String label}) {
  try {
    final decoded = jsonDecode(utf8.decode(bytes, allowMalformed: false));
    if (decoded is! Map) {
      throw FormatException('$label must be a JSON object.');
    }
    return Map<String, Object?>.from(decoded);
  } on FormatException {
    rethrow;
  } on Object catch (error) {
    throw FormatException('$label is invalid: $error');
  }
}

Uint8List _decodeBase64(
  String value, {
  required String label,
  required int expectedBytes,
}) {
  try {
    final bytes = base64Decode(value);
    if (bytes.length != expectedBytes ||
        base64Encode(bytes) != value.replaceAll(RegExp(r'\s+'), '')) {
      throw FormatException('$label is not canonical base64.');
    }
    return bytes;
  } on FormatException {
    rethrow;
  } on Object catch (error) {
    throw FormatException('$label is invalid: $error');
  }
}
