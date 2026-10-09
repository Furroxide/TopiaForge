import 'dart:convert';
import 'dart:io';

import 'package:path/path.dart' as p;

/// Keeps signed handoff assertions independent of the selected release mode.
/// The policy pin is synthetic; these tests do not create real signatures.
String writeSignedReleaseHandoffRoot(Directory parent) {
  var source = Directory.current.absolute;
  while (!File(p.join(source.path, 'TopiaForge.slnx')).existsSync()) {
    if (source.parent.path == source.path) {
      throw StateError('Repository root not found.');
    }
    source = source.parent;
  }
  final candidate = Directory(p.join(parent.path, 'signed-policy-root'))
    ..createSync();
  for (final relative in const [
    'release/catalog.json',
    'release/platform-toolchains.json',
    '.github/robotopia-game-build.json',
    'tests/live-game-acceptance.json',
  ]) {
    final destination = File(p.join(candidate.path, relative))
      ..createSync(recursive: true);
    destination.writeAsBytesSync(
      File(p.join(source.path, relative)).readAsBytesSync(),
    );
  }
  final policy =
      jsonDecode(
            File(
              p.join(source.path, 'release', 'release-policy.json'),
            ).readAsStringSync(),
          )
          as Map<String, Object?>;
  policy['signingIdentities'] = {
    'windowsDistribution': 'signed',
    'windowsCertificateSha256': 'a' * 64,
  };
  File(p.join(candidate.path, 'release', 'release-policy.json'))
    ..createSync(recursive: true)
    ..writeAsStringSync(jsonEncode(policy));
  return candidate.path;
}
