part of '../local_launcher_repository.dart';

extension _UgcLiveSyncHelpers on LocalLauncherRepository {
  Future<String> _deployUgcLiveSyncConfig(
    GameInstall install,
    UgcLiveSyncSettings settings,
  ) async {
    final file = _ugcConfigFile(install);
    final existing = await _readUgcLiveSyncConfigMap(install) ?? const {};
    await _writeJsonFileAtomic(file, {
      ...existing,
      ...settings.toRuntimeConfig(),
    });
    await _deleteFileIfExists(_ugcCommandFile(install));
    return file.path;
  }

  Future<UgcLiveSyncCleanupReport> _cleanupUgcLiveSync(
    GameInstall install,
    UgcLiveSyncSettings fallbackSettings,
  ) async {
    final deployed = await _readUgcLiveSyncConfigMap(install);
    final settings = deployed == null
        ? fallbackSettings
        : UgcLiveSyncSettings.fromJson(deployed);
    final cleanupSettings = UgcLiveSyncSettings(
      transport: settings.transport,
      watchFolder: settings.watchFolder,
      syncServerUrl: settings.syncServerUrl,
      sceneId: settings.sceneId,
      maxSnapshotBytes: settings.maxSnapshotBytes,
      debounceMilliseconds: settings.debounceMilliseconds,
    );
    final configPath = await _deployUgcLiveSyncConfig(install, cleanupSettings);
    final commandFile = _ugcCommandFile(install);
    await _writeJsonFileAtomic(commandFile, {
      'schemaVersion': 2,
      'command': 'stop',
      'cleanup': true,
      'createdUtc': DateTime.now().toUtc().toIso8601String(),
    });

    final statusDeleted = await _deleteFileIfExists(_ugcStatusFile(install));
    final sessionDeleted = await _deleteFileIfExists(_ugcPublisherSessionFile);
    return UgcLiveSyncCleanupReport(
      configPath: configPath,
      commandPath: commandFile.path,
      statusFileDeleted: statusDeleted,
      sessionFileDeleted: sessionDeleted,
    );
  }

  Future<Map<String, Object?>?> _readUgcLiveSyncConfigMap(
    GameInstall install,
  ) async {
    final file = _ugcConfigFile(install);
    final type = FileSystemEntity.typeSync(file.path, followLinks: false);
    if (type == FileSystemEntityType.notFound) {
      return null;
    }
    final decoded = await _readJsonFileBounded(
      file,
      maxBytes: _maxUgcControlFileBytes,
      label: 'UGC live-sync config',
    );
    if (decoded is! Map) {
      throw const FormatException('UGC live-sync config must be an object.');
    }
    return decoded.map((key, value) => MapEntry(key.toString(), value));
  }

  Future<UgcLiveSyncStatusSnapshot?> _readUgcLiveSyncStatus(
    GameInstall install,
  ) async {
    final file = _ugcStatusFile(install);
    final type = FileSystemEntity.typeSync(file.path, followLinks: false);
    if (type == FileSystemEntityType.notFound) {
      return null;
    }
    final decoded = await _readJsonFileBounded(
      file,
      maxBytes: _maxUgcControlFileBytes,
      label: 'UGC live-sync status',
    );
    if (decoded is! Map) {
      throw const FormatException('UGC live-sync status must be an object.');
    }
    final snapshot = UgcLiveSyncStatusSnapshot.fromJson(
      decoded.map((key, value) => MapEntry(key.toString(), value)),
    );
    if (snapshot.schemaVersion != 2) {
      throw const FormatException(
        'UGC live-sync status must use schemaVersion 2.',
      );
    }
    return snapshot;
  }

  Future<UgcSceneInspectionResult> _inspectWatchFolderScenes(
    String watchFolder,
  ) async {
    if (watchFolder.trim().isEmpty) {
      return UgcSceneInspectionResult();
    }
    final dir = Directory(watchFolder);
    final directoryType = FileSystemEntity.typeSync(
      dir.path,
      followLinks: false,
    );
    if (directoryType == FileSystemEntityType.notFound) {
      return UgcSceneInspectionResult();
    }
    if (directoryType != FileSystemEntityType.directory) {
      return _ugcInspectionFailure(
        'UGC watch folder must be a regular directory.',
      );
    }

    UgcInspectionSource? source;
    try {
      final candidates = <_UgcSnapshotCandidate>[];
      var scannedEntries = 0;
      await for (final entity in dir.list(followLinks: false)) {
        scannedEntries += 1;
        if (scannedEntries > _maxUgcWatchEntries) {
          throw StateError(
            'UGC watch folder exceeds the '
            '$_maxUgcWatchEntries-entry scan limit.',
          );
        }
        final lower = p.basename(entity.path).toLowerCase();
        if (!lower.endsWith('.json') && !lower.endsWith('.json.gz')) {
          continue;
        }
        final type = await FileSystemEntity.type(
          entity.path,
          followLinks: false,
        );
        if (type != FileSystemEntityType.file) {
          throw StateError(
            'UGC snapshot candidate is a symbolic link or special file: '
            '${p.basename(entity.path)}.',
          );
        }
        final file = File(entity.path);
        final stat = await file.stat();
        if (await FileSystemEntity.type(file.path, followLinks: false) !=
            FileSystemEntityType.file) {
          throw StateError('UGC snapshot changed while it was scanned.');
        }
        candidates.add(_UgcSnapshotCandidate(file, stat));
      }
      if (candidates.isEmpty) {
        return UgcSceneInspectionResult();
      }
      candidates.sort(_compareUgcCandidates);
      final selected = candidates.first;
      source = UgcInspectionSource(
        path: selected.file.path,
        modifiedAtUtc: selected.stat.modified.toUtc(),
        byteLength: selected.stat.size,
        compressed: selected.file.path.toLowerCase().endsWith('.gz'),
      );
      await _ugcInspectionReadHook?.call(selected.file.path);
      final stableBytes = await _readStableUgcSnapshot(selected);
      var bytes = stableBytes;
      final compressed =
          selected.file.path.toLowerCase().endsWith('.gz') ||
          (bytes.length >= 2 && bytes[0] == 0x1f && bytes[1] == 0x8b);
      source = UgcInspectionSource(
        path: source.path,
        modifiedAtUtc: source.modifiedAtUtc,
        byteLength: source.byteLength,
        compressed: compressed,
      );
      if (compressed) {
        final output = OutputMemoryStream();
        final bounded = _BoundedArchiveOutput(
          output,
          maxBytes: _maxUgcExpandedSnapshotBytes,
          entryName: p.basename(selected.file.path),
        );
        GZipDecoder().decodeStream(
          InputMemoryStream(bytes),
          bounded,
          verify: true,
        );
        bytes = output.getBytes();
      }
      var text = utf8.decode(bytes, allowMalformed: false);
      if (text.isNotEmpty && text.codeUnitAt(0) == 0xfeff) {
        text = text.substring(1);
      }
      return UgcSceneInspectionResult(
        scenes: _parseUgcScenes(jsonDecode(text)),
        source: source,
      );
    } on Object catch (error) {
      return _ugcInspectionFailure(
        'UGC snapshot inspection failed: $error',
        source: source,
      );
    }
  }

  Future<Uint8List> _readStableUgcSnapshot(
    _UgcSnapshotCandidate selected,
  ) async {
    _requireUnchangedUgcSnapshot(selected.file, selected.stat);
    final first = await _readLauncherFileBounded(
      selected.file,
      _maxUgcCompressedSnapshotBytes,
    );
    final middle = selected.file.statSync();
    _requireUnchangedUgcSnapshot(selected.file, selected.stat);
    final second = await _readLauncherFileBounded(
      selected.file,
      _maxUgcCompressedSnapshotBytes,
    );
    _requireUnchangedUgcSnapshot(selected.file, middle);
    if (sha256.convert(first).toString() != sha256.convert(second).toString()) {
      throw StateError('UGC snapshot changed while it was being read.');
    }
    return Uint8List.fromList(second);
  }

  File _ugcConfigFile(GameInstall install) => File(
    p.join(_managerConfig(install).path, 'topiaforge.ugc.livesync.json'),
  );

  File _ugcStatusFile(GameInstall install) => File(
    p.join(_managerConfig(install).path, 'topiaforge.ugc.livesync.status.json'),
  );

  File _ugcCommandFile(GameInstall install) => File(
    p.join(
      _managerConfig(install).path,
      'topiaforge.ugc.livesync.command.json',
    ),
  );
}

class _UgcSnapshotCandidate {
  const _UgcSnapshotCandidate(this.file, this.stat);

  final File file;
  final FileStat stat;
}

int _compareUgcCandidates(
  _UgcSnapshotCandidate left,
  _UgcSnapshotCandidate right,
) {
  final byModified = right.stat.modified.compareTo(left.stat.modified);
  if (byModified != 0) {
    return byModified;
  }
  final leftName = p.basename(left.file.path);
  final rightName = p.basename(right.file.path);
  final byFoldedName = leftName.toLowerCase().compareTo(
    rightName.toLowerCase(),
  );
  return byFoldedName != 0
      ? byFoldedName
      : left.file.path.compareTo(right.file.path);
}

void _requireUnchangedUgcSnapshot(File file, FileStat expected) {
  final type = FileSystemEntity.typeSync(file.path, followLinks: false);
  if (type != FileSystemEntityType.file) {
    throw StateError('UGC snapshot changed while it was being inspected.');
  }
  final actual = file.statSync();
  if (actual.size != expected.size || actual.modified != expected.modified) {
    throw StateError('UGC snapshot changed while it was being inspected.');
  }
}

UgcSceneInspectionResult _ugcInspectionFailure(
  String message, {
  UgcInspectionSource? source,
}) => UgcSceneInspectionResult(
  source: source,
  issues: [LauncherIssue(severity: IssueSeverity.error, message: message)],
);

List<UgcSceneRef> _parseUgcScenes(Object? decoded) {
  if (decoded is! Map<String, Object?>) {
    throw const FormatException('UGC project snapshot must be an object.');
  }
  final scenes = decoded['scenes'];
  if (scenes is! Map) {
    throw const FormatException(
      'UGC project snapshot must contain a scenes object.',
    );
  }
  if (scenes.length > _maxUgcScenes) {
    throw StateError('UGC project snapshot exceeds the scene limit.');
  }
  final result = <UgcSceneRef>[];
  final seen = <String>{};
  for (final entry in scenes.entries) {
    final value = entry.value;
    final id = value is Map && value['id'] is String
        ? value['id'] as String
        : entry.key.toString();
    final name = value is Map && value['name'] is String
        ? value['name'] as String
        : '';
    if (!_isValidUgcSceneText(id) || !_isValidUgcSceneText(name, empty: true)) {
      throw const FormatException(
        'UGC project snapshot contains an invalid scene id or name.',
      );
    }
    if (!seen.add(id.toLowerCase())) {
      throw FormatException('UGC project snapshot repeats scene id $id.');
    }
    result.add(UgcSceneRef(id: id, name: name));
  }
  result.sort((left, right) => left.label.compareTo(right.label));
  return result;
}

bool _isValidUgcSceneText(String value, {bool empty = false}) {
  if ((!empty && value.trim().isEmpty) || value.length > 512) {
    return false;
  }
  return !value.contains(RegExp(r'[\u0000-\u001f\u007f]'));
}

const _maxUgcControlFileBytes = 1024 * 1024;
const _maxUgcWatchEntries = 4096;
const _maxUgcCompressedSnapshotBytes = 16 * 1024 * 1024;
const _maxUgcExpandedSnapshotBytes = 32 * 1024 * 1024;
const _maxUgcScenes = 10000;

Future<bool> _deleteFileIfExists(File file) async {
  if (!await file.exists()) {
    return false;
  }
  try {
    await file.delete();
    return true;
  } on FileSystemException {
    // Another process may consume a one-shot command between exists/delete.
    if (!await file.exists()) {
      return false;
    }
    rethrow;
  }
}
