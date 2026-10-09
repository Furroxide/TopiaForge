import 'dart:io';

import 'bounded_file_reader.dart';
import 'release_policy.dart';

Map<String, Object?> readReleaseCodeSigningEvidence(
  File file,
  TopiaForgeReleasePolicy policy,
) {
  final json = readBoundedJsonObjectSync(
    file,
    maxBytes: CliFileLimits.metadata,
  );
  final platforms = policy.targetPlatforms.toSet();
  if (!(json.keys.length == platforms.length &&
      json.keys.toSet().containsAll(platforms))) {
    throw StateError(
      '.platform-trust-evidence.json must '
      'contain the exact policy target platform set.',
    );
  }
  final normalized = <String, Object?>{};
  for (final platform in platforms.toList()..sort()) {
    final value = json[platform];
    if (value is! Map) {
      throw StateError('Code-signing evidence for $platform is invalid.');
    }
    final entry = Map<String, Object?>.from(value);
    final status = entry['status'];
    final exceptionApplied = entry['exceptionApplied'];
    if (entry.length != 2 || status is! String || exceptionApplied is! bool) {
      throw StateError('Code-signing evidence for $platform is invalid.');
    }
    switch (platform) {
      case 'windows-x64':
        if (status == 'trusted' && !exceptionApplied) break;
        throw StateError(
          'Windows code-signing evidence is not permitted by release policy.',
        );
      case 'macos-universal':
        if (status == 'trusted' && !exceptionApplied) break;
        throw StateError(
          'macOS code-signing evidence is not permitted by release policy.',
        );
      case 'linux-x64':
        if (status != 'not-applicable' || exceptionApplied) {
          throw StateError('Linux code-signing evidence is invalid.');
        }
    }
    normalized[platform] = {
      'status': status,
      'exceptionApplied': exceptionApplied,
    };
  }
  return {'exceptionVersion': null, 'platforms': normalized};
}
