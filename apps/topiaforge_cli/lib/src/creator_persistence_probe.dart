import 'dart:convert';
import 'dart:io';

import 'package:crypto/crypto.dart';
import 'package:path/path.dart' as p;

import 'creator_acceptance_models.dart';

/// Captures real on-disk Robotopia save and checkpoint bytes around End Session.
///
/// Build-2309 persists player state to `player_data.json.gz` in the Unity
/// persistent data path. That single document carries both the save payload and
/// the checkpoint cursor (`CURRENT_CHECKPOINT` plus `<id>_reached` flags), so
/// both halves of the persistence gate are observed from real bytes rather than
/// asserted by a script.
///
/// Two properties matter for correctness:
///
/// * The document is gzip-compressed, and a gzip member embeds an mtime in its
///   header. Digesting raw bytes would report spurious changes, so the probe
///   always digests canonicalized decompressed content.
/// * A save file introduced, renamed, or relocated by a later game build must
///   not silently pass. The probe therefore digests the whole declared root
///   minus a declared volatile exclusion set, and records both lists in the
///   evidence so a narrowed scope is visible to the verifier.
final class CreatorPersistenceProbe {
  const CreatorPersistenceProbe({this.rootOverride = ''});

  /// Unity company/product folder for Robotopia under `AppData/LocalLow`.
  static const String unityCompany = 'Tomato Cake';
  static const String unityProduct = 'robotopia';

  /// Save document observed for build-2309.
  static const String saveDocument = 'player_data.json.gz';

  /// Declared volatile members excluded from the persistence digest. Every
  /// entry is recorded in the evidence so a narrowed exclusion set is visible.
  static const List<String> volatileExclusions = [
    'Player.log',
    'Player-prev.log',
    'launcher.log',
    'robo_token.json',
    'sentry-unity.lock',
    'PostHog/',
    'Sentry/',
    'SentryNative/',
  ];

  /// Save keys that carry checkpoint progress rather than settings.
  static const String checkpointKey = 'CURRENT_CHECKPOINT';
  static const String checkpointReachedSuffix = '_reached';

  static const int _maximumMembers = 8192;
  static const int _maximumMemberBytes = 64 * 1024 * 1024;

  /// Overrides the resolved root; tests use this to supply a fixture tree.
  final String rootOverride;

  /// Resolves the declared persistence root for this host.
  String resolveRoot() {
    if (rootOverride.trim().isNotEmpty) {
      return p.normalize(p.absolute(rootOverride));
    }
    final localAppData = Platform.environment['LOCALAPPDATA'] ?? '';
    if (localAppData.trim().isEmpty) return '';
    return p.normalize(
      p.join(p.dirname(localAppData), 'LocalLow', unityCompany, unityProduct),
    );
  }

  /// Declares the layout actually used for this capture.
  CreatorPersistenceLayout describeLayout() => CreatorPersistenceLayout(
    version: CreatorPersistenceLayout.currentVersion,
    roots: List.unmodifiable([
      p.join('%LOCALAPPDATA%', '..', 'LocalLow', unityCompany, unityProduct),
    ]),
    exclusions: List.unmodifiable(volatileExclusions),
  );

  /// Digests the declared root into separate save and checkpoint digests.
  ///
  /// Throws when the declared root or the save document is absent, which is the
  /// fail-closed path for a game build that relocated its persisted state.
  ({
    CreatorStateDigest save,
    CreatorStateDigest checkpoint,
    List<int> saveDocumentBytes,
    List<int> checkpointDocumentBytes,
  })
  capture() {
    final root = resolveRoot();
    if (root.isEmpty ||
        FileSystemEntity.typeSync(root, followLinks: false) !=
            FileSystemEntityType.directory) {
      throw CreatorAcceptanceError(
        'TFCREATOR140',
        'The declared Robotopia persistence root does not exist: $root',
        'Confirm the installed build still uses the declared Unity persistent '
            'data path, then update CreatorPersistenceProbe and bump '
            'CreatorPersistenceLayout.currentVersion together.',
      );
    }
    final members = <String, Map<String, Object?>>{};
    var count = 0;
    for (final entity in Directory(
      root,
    ).listSync(recursive: true, followLinks: false)) {
      if (entity is! File) continue;
      final relative = p
          .relative(entity.path, from: root)
          .replaceAll(r'\', '/');
      if (_isExcluded(relative)) continue;
      if (++count > _maximumMembers) {
        throw const CreatorAcceptanceError(
          'TFCREATOR141',
          'The Robotopia persistence root exceeds its member limit.',
          'Investigate unexpected growth before trusting Creator evidence.',
        );
      }
      if (entity.lengthSync() > _maximumMemberBytes) {
        throw CreatorAcceptanceError(
          'TFCREATOR141',
          'Persistence member exceeds its size limit: $relative',
          'Investigate unexpected growth before trusting Creator evidence.',
        );
      }
      final content = _canonicalMemberBytes(entity, relative);
      members[relative] = {
        'sha256': sha256.convert(content).toString(),
        'size': content.length,
      };
    }
    if (!members.containsKey(saveDocument)) {
      throw CreatorAcceptanceError(
        'TFCREATOR142',
        'The Robotopia save document is missing: $saveDocument',
        'Play far enough for Robotopia to write a save, or update the declared '
            'save document and bump CreatorPersistenceLayout.currentVersion.',
      );
    }
    // The canonical documents are returned alongside their digests so retained
    // evidence can carry a byte-verifiable pre-image. Neither document repeats
    // raw save bytes, so machine-specific values such as the recorded audio
    // device never reach the retained bundle.
    final saveBytes = utf8.encode(
      jsonEncode({
        'layoutVersion': CreatorPersistenceLayout.currentVersion,
        'members': [
          for (final key in members.keys.toList()..sort())
            {'path': key, ...members[key]!},
        ],
      }),
    );
    final checkpointBytes = utf8.encode(
      jsonEncode(_readCheckpointState(p.join(root, saveDocument))),
    );
    return (
      save: _digestOf(saveBytes),
      checkpoint: _digestOf(checkpointBytes),
      saveDocumentBytes: saveBytes,
      checkpointDocumentBytes: checkpointBytes,
    );
  }

  /// Decompresses gzip members so a re-written header cannot look like a change.
  List<int> _canonicalMemberBytes(File file, String relative) {
    final raw = file.readAsBytesSync();
    if (!relative.endsWith('.gz')) return raw;
    try {
      return gzip.decode(raw);
    } on Object {
      throw CreatorAcceptanceError(
        'TFCREATOR143',
        'A gzip persistence member could not be decompressed: $relative',
        'Confirm the installed build still writes a valid gzip document.',
      );
    }
  }

  /// Extracts only the checkpoint cursor and reached flags from the save.
  Map<String, Object?> _readCheckpointState(String savePath) {
    final decoded = jsonDecode(
      utf8.decode(gzip.decode(File(savePath).readAsBytesSync())),
    );
    final data = decoded is Map ? decoded['data'] : null;
    if (decoded is! Map || data is! Map) {
      throw const CreatorAcceptanceError(
        'TFCREATOR144',
        'The Robotopia save document does not have the expected shape.',
        'Confirm the installed build still writes {"version",...,"data"{...}}, '
            'then bump CreatorPersistenceLayout.currentVersion.',
      );
    }
    final checkpoint = <String, Object?>{};
    for (final key in data.keys.map((key) => '$key').toList()..sort()) {
      if (key == checkpointKey || key.endsWith(checkpointReachedSuffix)) {
        checkpoint[key] = data[key];
      }
    }
    return {
      'saveVersion': decoded['version'],
      'layoutVersion': CreatorPersistenceLayout.currentVersion,
      'checkpoint': checkpoint,
    };
  }

  static CreatorStateDigest _digestOf(List<int> bytes) => CreatorStateDigest(
    sha256: sha256.convert(bytes).toString(),
    size: bytes.length,
  );

  static bool _isExcluded(String relativePath) {
    for (final exclusion in volatileExclusions) {
      if (exclusion.endsWith('/')) {
        if (relativePath.startsWith(exclusion)) return true;
      } else if (relativePath == exclusion) {
        return true;
      }
    }
    return false;
  }
}
