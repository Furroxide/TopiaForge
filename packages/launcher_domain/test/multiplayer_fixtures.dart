import 'dart:convert';
import 'dart:io';
import 'dart:typed_data';

import 'package:launcher_domain/launcher_domain.dart';

/// Reads `tests/fixtures/multiplayer/<relative>`, which the game's C# tests
/// and the control plane replay too.
Map<String, Object?> readMultiplayerFixture(String relative) {
  var directory = Directory.current.absolute;
  while (!File('${directory.path}/TopiaForge.slnx').existsSync()) {
    final parent = directory.parent;
    if (parent.path == directory.path) {
      throw StateError('Could not locate TopiaForge.slnx.');
    }
    directory = parent;
  }
  final path = [
    directory.path,
    'tests',
    'fixtures',
    'multiplayer',
    ...relative.split('/'),
  ].join(Platform.pathSeparator);
  return asObject(jsonDecode(File(path).readAsStringSync()));
}

Map<String, Object?> asObject(Object? value) =>
    (value! as Map).map((key, item) => MapEntry(key as String, item));

List<Map<String, Object?>> asObjects(Object? value) =>
    (value! as List<Object?>).map(asObject).toList();

Uint8List hexBytes(String text) => Uint8List.fromList([
  for (var index = 0; index < text.length; index += 2)
    int.parse(text.substring(index, index + 2), radix: 16),
]);

Uint8List textBytes(String text) => Uint8List.fromList(utf8.encode(text));

/// Builds a descriptor from a fixture's descriptor object, member by member,
/// as the C# tests do, so the model's own writer is what gets compared.
ServerDescriptorV1 descriptorFromFixture(Map<String, Object?> value) =>
    ServerDescriptorV1(
      signingKey: WireText.tryParseBase64Url(
        value['signingKey']! as String,
        32,
      )!,
      transportKey: WireText.tryParseBase64Url(
        value['transportKey']! as String,
        32,
      )!,
      issuedAt: value['issuedAt']! as int,
      name: value['name']! as String,
      gameBuild: value['gameBuild']! as String,
      runtimeVersion: value['runtimeVersion']! as String,
      protocolVersion: value['protocolVersion']! as String,
      maxPlayers: value['maxPlayers']! as int,
      passwordRequired: value['passwordRequired']! as bool,
      voice: value['voice']! as bool,
      visibility: ServerVisibility.tryParse(value['visibility']! as String)!,
      hostServesPackages: value['hostServesPackages']! as bool,
      gamemode: value['gamemode'] as String?,
      world: value['world'] as String?,
      packages: [
        for (final item in asObjects(value['packages']))
          DescriptorPackage(
            id: item['id']! as String,
            version: item['version']! as String,
            sha256: item['sha256']! as String,
            role: DescriptorPackageRole.tryParse(item['role']! as String)!,
          ),
      ],
    );

/// A signature verifier for domain tests, which cannot run Ed25519: it
/// accepts exactly the message and signature pairs it was given, so a test
/// shows the domain asks about the right bytes. `launcher_data` replays the
/// same fixtures with real Ed25519.
final class KnownSignatures implements ServerDescriptorSignatureVerifier {
  KnownSignatures(Iterable<(List<int> message, List<int> signature)> pairs)
    : _pairs = {
        for (final (message, signature) in pairs)
          '${base64Encode(message)}:${base64Encode(signature)}',
      };

  /// Every valid descriptor fixture's message and signature.
  factory KnownSignatures.fromFixture(Map<String, Object?> fixture) {
    final context = textBytes(fixture['context']! as String);
    return KnownSignatures([
      for (final item in asObjects(fixture['valid']))
        (
          [...context, ...textBytes(item['canonicalDescriptor']! as String)],
          WireText.tryParseBase64Url(item['signature']! as String, 64)!,
        ),
    ]);
  }

  final Set<String> _pairs;
  final List<SignedServerDescriptorV1> asked = [];

  @override
  Future<bool> verifySignature(SignedServerDescriptorV1 envelope) async {
    asked.add(envelope);
    return _pairs.contains(
      '${base64Encode(envelope.signedMessage)}:'
      '${base64Encode(envelope.signature)}',
    );
  }
}

/// Accepts every signature, to build verified descriptors for planning tests.
final class AcceptAllSignatures implements ServerDescriptorSignatureVerifier {
  const AcceptAllSignatures();

  @override
  Future<bool> verifySignature(SignedServerDescriptorV1 envelope) async => true;
}

/// Wraps [descriptor] in an envelope with a placeholder signature and
/// verifies it with [AcceptAllSignatures].
Future<VerifiedServerDescriptor> verifiedForTest(
  ServerDescriptorV1 descriptor,
) async {
  final envelope = SignedServerDescriptorV1.assemble(
    descriptor,
    Uint8List(SignedServerDescriptorV1.signatureBytes),
  );
  final result = await const ServerDescriptorVerifier(
    AcceptAllSignatures(),
  ).verify(envelope.bytes);
  return (result as ServerDescriptorAccepted).descriptor;
}
