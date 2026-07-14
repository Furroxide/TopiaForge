part of '../models.dart';

class UnityCompanionSettings {
  const UnityCompanionSettings({
    this.enabled = false,
    this.projectPath = '',
    this.unityVersion = '',
    this.assetBundleOutputPath = '',
    this.liveSync = const UgcLiveSyncSettings(),
  });

  final bool enabled;
  final String projectPath;
  final String unityVersion;
  final String assetBundleOutputPath;

  /// UGC content live-sync settings (the Dart mirror of the C# `UgcLiveSyncConfig`).
  final UgcLiveSyncSettings liveSync;

  factory UnityCompanionSettings.fromJson(Map<String, Object?> json) {
    return UnityCompanionSettings(
      enabled: (json['enabled'] as bool?) ?? false,
      projectPath: (json['projectPath'] as String?) ?? '',
      unityVersion: (json['unityVersion'] as String?) ?? '',
      assetBundleOutputPath: (json['assetBundleOutputPath'] as String?) ?? '',
      liveSync: UgcLiveSyncSettings.fromJson(_objectMap(json['liveSync'])),
    );
  }

  Map<String, Object?> toJson() => {
    'enabled': enabled,
    if (projectPath.isNotEmpty) 'projectPath': projectPath,
    if (unityVersion.isNotEmpty) 'unityVersion': unityVersion,
    if (assetBundleOutputPath.isNotEmpty)
      'assetBundleOutputPath': assetBundleOutputPath,
    'liveSync': liveSync.toJson(),
  };
}

/// UGC content live-sync settings. The Dart source of truth for the launcher/CLI, and the mirror of the C#
/// `TopiaForge.UgcLiveSync.UgcLiveSyncConfig`. [toRuntimeConfig] produces the exact JSON the game mod reads from
/// `config/topiaforge.ugc.livesync.json`; a contract test pins these keys to the C# `[DataMember]` names.
class UgcLiveSyncSettings {
  const UgcLiveSyncSettings({
    this.transport = 'localFolder',
    this.watchFolder = '',
    this.editorUrl = '',
    this.documentUrl = '',
    this.syncServerUrl = defaultSyncServerUrl,
    this.sceneId = '',
    this.autoConnectOnStart = false,
    this.maxSnapshotBytes = defaultMaxSnapshotBytes,
    this.debounceMilliseconds = 200,
  });

  static const String defaultSyncServerUrl =
      'https://automerge-repo-sync-server-main.onrender.com';
  static const int defaultMaxSnapshotBytes = 16 * 1024 * 1024;

  final String transport;
  final String watchFolder;
  final String editorUrl;
  final String documentUrl;
  final String syncServerUrl;
  final String sceneId;
  final bool autoConnectOnStart;
  final int maxSnapshotBytes;
  final int debounceMilliseconds;

  /// Clamps an arbitrary value to one of the two supported transports (mirrors the C# side).
  static String normalizeTransport(String? value) {
    return (value ?? '').toLowerCase() == 'automerge'
        ? 'automerge'
        : 'localFolder';
  }

  factory UgcLiveSyncSettings.fromJson(Map<String, Object?> json) {
    return UgcLiveSyncSettings(
      transport: normalizeTransport(json['transport'] as String?),
      watchFolder: (json['watchFolder'] as String?) ?? '',
      editorUrl: (json['editorUrl'] as String?) ?? '',
      documentUrl: (json['documentUrl'] as String?) ?? '',
      syncServerUrl: (json['syncServerUrl'] as String?) ?? defaultSyncServerUrl,
      sceneId: (json['sceneId'] as String?) ?? '',
      autoConnectOnStart: (json['autoConnectOnStart'] as bool?) ?? false,
      maxSnapshotBytes:
          (json['maxSnapshotBytes'] as num?)?.toInt() ??
          defaultMaxSnapshotBytes,
      debounceMilliseconds:
          (json['debounceMilliseconds'] as num?)?.toInt() ?? 200,
    );
  }

  /// Sparse persistence inside `topiaforge.project.json` (only non-default values).
  Map<String, Object?> toJson() => {
    'transport': normalizeTransport(transport),
    if (watchFolder.isNotEmpty) 'watchFolder': watchFolder,
    if (editorUrl.isNotEmpty) 'editorUrl': editorUrl,
    if (documentUrl.isNotEmpty) 'documentUrl': documentUrl,
    if (syncServerUrl != defaultSyncServerUrl) 'syncServerUrl': syncServerUrl,
    if (sceneId.isNotEmpty) 'sceneId': sceneId,
    if (autoConnectOnStart) 'autoConnectOnStart': autoConnectOnStart,
    if (maxSnapshotBytes != defaultMaxSnapshotBytes)
      'maxSnapshotBytes': maxSnapshotBytes,
    if (debounceMilliseconds != 200)
      'debounceMilliseconds': debounceMilliseconds,
  };

  /// The full runtime config the game mod reads. Keys MUST equal the C# `UgcLiveSyncConfig` `[DataMember]` names.
  Map<String, Object?> toRuntimeConfig() => {
    'transport': normalizeTransport(transport),
    'watchFolder': watchFolder,
    'editorUrl': editorUrl,
    'documentUrl': documentUrl,
    'syncServerUrl': syncServerUrl,
    'sceneId': sceneId,
    'autoConnectOnStart': autoConnectOnStart,
    'maxSnapshotBytes': maxSnapshotBytes,
    'debounceMilliseconds': debounceMilliseconds,
  };
}

/// A scene the UGC editor/companion exposes (id + display name), surfaced as a dropdown in the live-sync cockpit
/// so the developer picks a scene instead of typing its id. Parsed from the newest exported project JSON in the
/// watch folder, or from the game's status handshake.
class UgcSceneRef {
  const UgcSceneRef({required this.id, this.name = ''});

  final String id;
  final String name;

  String get label => name.isEmpty ? id : '$name ($id)';
}

/// The stable file snapshot used for a UGC scene inspection.
class UgcInspectionSource {
  const UgcInspectionSource({
    required this.path,
    required this.modifiedAtUtc,
    required this.byteLength,
    required this.compressed,
  });

  final String path;
  final DateTime modifiedAtUtc;
  final int byteLength;
  final bool compressed;
}

/// A non-throwing inspection result for UGC watch-folder discovery.
class UgcSceneInspectionResult {
  UgcSceneInspectionResult({
    List<UgcSceneRef> scenes = const [],
    this.source,
    List<LauncherIssue> issues = const [],
  }) : scenes = List.unmodifiable(scenes),
       issues = List.unmodifiable(issues);

  final List<UgcSceneRef> scenes;
  final UgcInspectionSource? source;
  final List<LauncherIssue> issues;

  bool get hasBlockingIssues => issues.any((issue) => issue.isBlocking);
}

/// The game → launcher status handshake the UGC live-sync mod writes to
/// `config/topiaforge.ugc.livesync.status.json`. Lets the cockpit auto-detect the game's default watch folder and
/// show live diagnostics without guessing. Mirrors the C# `UgcLiveSyncStatusFile` DTO (keys are a contract).
class UgcLiveSyncStatusSnapshot {
  const UgcLiveSyncStatusSnapshot({
    this.schemaVersion = 2,
    this.status = 'Idle',
    this.transport = 'localFolder',
    this.defaultWatchFolder = '',
    this.watchFolder = '',
    this.connectedDocumentUrl = '',
    this.sceneId = '',
    this.availableScenes = const [],
    this.lastAppliedUtc = '',
    this.modVersion = '',
    this.updatedUtc = '',
  });

  final int schemaVersion;
  final String status;
  final String transport;
  final String defaultWatchFolder;
  final String watchFolder;
  final String connectedDocumentUrl;
  final String sceneId;
  final List<String> availableScenes;
  final String lastAppliedUtc;
  final String modVersion;
  final String updatedUtc;

  /// True when the game is actively syncing (an Automerge session or a folder watch is live).
  bool get isLive => status == 'Connected' || status == 'Watching';

  factory UgcLiveSyncStatusSnapshot.fromJson(Map<String, Object?> json) {
    return UgcLiveSyncStatusSnapshot(
      schemaVersion: (json['schemaVersion'] as num?)?.toInt() ?? 0,
      status: (json['status'] as String?) ?? 'Idle',
      transport: (json['transport'] as String?) ?? 'localFolder',
      defaultWatchFolder: (json['defaultWatchFolder'] as String?) ?? '',
      watchFolder: (json['watchFolder'] as String?) ?? '',
      connectedDocumentUrl: (json['connectedDocumentUrl'] as String?) ?? '',
      sceneId: (json['sceneId'] as String?) ?? '',
      availableScenes: _stringList(json['availableScenes']),
      lastAppliedUtc: (json['lastAppliedUtc'] as String?) ?? '',
      modVersion: (json['modVersion'] as String?) ?? '',
      updatedUtc: (json['updatedUtc'] as String?) ?? '',
    );
  }
}
