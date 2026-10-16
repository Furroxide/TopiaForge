import 'dart:convert';
import 'dart:io';

import 'package:crypto/crypto.dart';
import 'package:path/path.dart' as p;

import 'release_git_contract.dart';
import 'release_strict_json.dart';

const candidateReadinessName = 'release-candidate-readiness-v1.json';
const candidateAcceptanceName = 'release-candidate-acceptance-v1.json';
const candidateReadinessSchemaPath =
    'schemas/topiaforge.release-candidate-readiness-v1.schema.json';
const candidateAcceptanceSchemaPath =
    'schemas/topiaforge.release-candidate-acceptance-v1.schema.json';

/// Fixed paths only: no candidate-controlled filesystem or network references.
const candidateContractPaths = [
  '.github/robotopia-game-build.json',
  'release/catalog.json',
  'release/platform-toolchains.json',
  'release/release-policy.json',
  'release/release-readiness.json',
  'schemas/topiaforge.release-candidate-acceptance-v1.schema.json',
  'schemas/topiaforge.release-candidate-readiness-v1.schema.json',
  'schemas/topiaforge.release-catalog.schema.json',
  'schemas/topiaforge.release-policy.schema.json',
  'schemas/topiaforge.release-readiness-v1.schema.json',
  'tests/gamemode-release-acceptance.json',
  'tests/live-game-acceptance.json',
];

final class ReleaseCandidateContract {
  ReleaseCandidateContract._(this.blobs);
  final Map<String, List<int>> blobs;

  static Future<ReleaseCandidateContract> load(String root, String sha) async {
    if (!RegExp(r'^[0-9a-f]{40}$').hasMatch(sha)) {
      throw StateError(
        'Candidate requires an exact lowercase 40-character SHA.',
      );
    }
    final resolved = await Process.run(
      'git',
      [
        '--no-replace-objects',
        '-C',
        root,
        'rev-parse',
        '--verify',
        '$sha^{commit}',
      ],
      stdoutEncoding: utf8,
      stderrEncoding: utf8,
    );
    if (resolved.exitCode != 0 || (resolved.stdout as String).trim() != sha) {
      throw StateError('Candidate target is not the exact requested commit.');
    }
    final blobs = <String, List<int>>{};
    for (final path in candidateContractPaths) {
      final bytes = await readReleaseGitBlob(root, sha, path, 256 * 1024);
      // Reject information-losing JSON before any legacy snapshot reader can
      // deserialize it. Keep the original immutable bytes as the identity.
      decodeReleaseObject(bytes, maximumBytes: 256 * 1024, label: path);
      blobs[path] = bytes;
    }
    return ReleaseCandidateContract._(Map.unmodifiable(blobs));
  }

  String digest(String path) => sha256.convert(blobs[path]!).toString();
  Map<String, Object?> object(String path) =>
      decodeReleaseObject(blobs[path]!, maximumBytes: 256 * 1024, label: path);
  String get contractSha256 => sha256
      .convert(
        utf8.encode(
          candidateContractPaths
              .map((path) => '$path\u0000${digest(path)}\n')
              .join(),
        ),
      )
      .toString();

  /// Existing handoff validation reads only this owned, exact-SHA snapshot.
  Future<T> withSnapshot<T>(Future<T> Function(String root) action) async {
    final directory = Directory.systemTemp.createTempSync(
      'topiaforge-contract-',
    );
    try {
      for (final entry in blobs.entries) {
        final file = File(p.join(directory.path, entry.key));
        file.parent.createSync(recursive: true);
        file.writeAsBytesSync(entry.value, flush: true);
      }
      return await action(directory.path);
    } finally {
      directory.deleteSync(recursive: true);
    }
  }
}
