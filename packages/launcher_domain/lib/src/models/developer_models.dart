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
/// `Robotopia.UgcLiveSync.UgcLiveSyncConfig`. [toRuntimeConfig] produces the exact JSON the game mod reads from
/// `config/robotopia.ugc.livesync.json`; a contract test pins these keys to the C# `[DataMember]` names.
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

  /// Sparse persistence inside `robotopia.project.json` (only non-default values).
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

/// The game → launcher status handshake the UGC live-sync mod writes to
/// `config/robotopia.ugc.livesync.status.json`. Lets the cockpit auto-detect the game's default watch folder and
/// show live diagnostics without guessing. Mirrors the C# `UgcLiveSyncStatusFile` DTO (keys are a contract).
class UgcLiveSyncStatusSnapshot {
  const UgcLiveSyncStatusSnapshot({
    this.schemaVersion = 1,
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
      schemaVersion: (json['schemaVersion'] as num?)?.toInt() ?? 1,
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

class DeveloperProject {
  const DeveloperProject({
    required this.schemaVersion,
    required this.id,
    required this.name,
    this.type = 'mod',
    this.dependencies = const [],
    this.optionalDependencies = const [],
    this.packageSources = const [],
    this.gameVersionRange = const VersionRange.any(),
    this.loaderVersionRange = const VersionRange.any(),
    this.unityCompanion = const UnityCompanionSettings(),
  });

  final int schemaVersion;
  final String id;
  final String name;
  final String type;
  final List<ModDependency> dependencies;
  final List<ModDependency> optionalDependencies;
  final List<PackageSource> packageSources;
  final VersionRange gameVersionRange;
  final VersionRange loaderVersionRange;
  final UnityCompanionSettings unityCompanion;

  /// Returns a copy with [liveSync] merged into the Unity companion settings (enabling the companion).
  DeveloperProject withUgcLiveSync(UgcLiveSyncSettings liveSync) {
    return copyWith(
      unityCompanion: UnityCompanionSettings(
        enabled: true,
        projectPath: unityCompanion.projectPath,
        unityVersion: unityCompanion.unityVersion,
        assetBundleOutputPath: unityCompanion.assetBundleOutputPath,
        liveSync: liveSync,
      ),
    );
  }

  factory DeveloperProject.fromJson(Map<String, Object?> json) {
    return DeveloperProject(
      schemaVersion: (json['schemaVersion'] as num?)?.toInt() ?? 0,
      id: (json['id'] as String?) ?? '',
      name: (json['name'] as String?) ?? '',
      type: (json['type'] as String?) ?? 'mod',
      dependencies: _dependencyList(json['dependencies']),
      optionalDependencies: _dependencyList(json['optionalDependencies']),
      packageSources: _packageSourceList(json['packageSources']),
      gameVersionRange: VersionRange.parse(
        (json['supportedGameVersionRange'] as String?) ??
            (json['gameVersionRange'] as String?),
      ),
      loaderVersionRange: VersionRange.parse(
        (json['supportedLoaderVersionRange'] as String?) ??
            (json['loaderVersionRange'] as String?),
      ),
      unityCompanion: UnityCompanionSettings.fromJson(
        _objectMap(json['unityCompanion']),
      ),
    );
  }

  Map<String, Object?> toJson() => {
    'schemaVersion': schemaVersion,
    'id': id,
    'name': name,
    'type': type,
    if (dependencies.isNotEmpty)
      'dependencies': dependencies.map((item) => item.toJson()).toList(),
    if (optionalDependencies.isNotEmpty)
      'optionalDependencies': optionalDependencies
          .map((item) => item.toJson())
          .toList(),
    if (packageSources.isNotEmpty)
      'packageSources': packageSources.map((item) => item.toJson()).toList(),
    if (!gameVersionRange.isAny)
      'supportedGameVersionRange': gameVersionRange.toString(),
    if (!loaderVersionRange.isAny)
      'supportedLoaderVersionRange': loaderVersionRange.toString(),
    if (unityCompanion.enabled) 'unityCompanion': unityCompanion.toJson(),
  };

  DeveloperProject copyWith({
    List<ModDependency>? dependencies,
    List<ModDependency>? optionalDependencies,
    List<PackageSource>? packageSources,
    UnityCompanionSettings? unityCompanion,
  }) {
    return DeveloperProject(
      schemaVersion: schemaVersion,
      id: id,
      name: name,
      type: type,
      dependencies: dependencies ?? this.dependencies,
      optionalDependencies: optionalDependencies ?? this.optionalDependencies,
      packageSources: packageSources ?? this.packageSources,
      gameVersionRange: gameVersionRange,
      loaderVersionRange: loaderVersionRange,
      unityCompanion: unityCompanion ?? this.unityCompanion,
    );
  }
}

class LockedPackage {
  const LockedPackage({
    required this.id,
    required this.name,
    required this.version,
    required this.packageUrl,
    required this.packageSha256,
    this.sourceId = '',
    this.sourceName = '',
    this.dependencies = const [],
    this.apiAssemblies = const [],
    this.cachePath = '',
  });

  final String id;
  final String name;
  final String version;
  final String packageUrl;
  final String packageSha256;
  final String sourceId;
  final String sourceName;
  final List<String> dependencies;
  final List<String> apiAssemblies;
  final String cachePath;

  factory LockedPackage.fromJson(Map<String, Object?> json) {
    return LockedPackage(
      id: (json['id'] as String?) ?? '',
      name: (json['name'] as String?) ?? '',
      version: (json['version'] as String?) ?? '',
      packageUrl: (json['packageUrl'] as String?) ?? '',
      packageSha256: (json['packageSha256'] as String?) ?? '',
      sourceId: (json['sourceId'] as String?) ?? '',
      sourceName: (json['sourceName'] as String?) ?? '',
      dependencies: _stringList(json['dependencies']),
      apiAssemblies: _stringList(json['apiAssemblies']),
      cachePath: (json['cachePath'] as String?) ?? '',
    );
  }

  Map<String, Object?> toJson() => {
    'id': id,
    'name': name,
    'version': version,
    'packageUrl': packageUrl,
    if (packageSha256.isNotEmpty) 'packageSha256': packageSha256,
    if (sourceId.isNotEmpty) 'sourceId': sourceId,
    if (sourceName.isNotEmpty) 'sourceName': sourceName,
    if (dependencies.isNotEmpty) 'dependencies': dependencies,
    if (apiAssemblies.isNotEmpty) 'apiAssemblies': apiAssemblies,
    if (cachePath.isNotEmpty) 'cachePath': cachePath,
  };
}

class DeveloperLock {
  const DeveloperLock({
    required this.schemaVersion,
    required this.projectId,
    required this.resolvedAtUtc,
    required this.packages,
    this.dependencyGraph = const {},
  });

  final int schemaVersion;
  final String projectId;
  final String resolvedAtUtc;
  final List<LockedPackage> packages;
  final Map<String, List<String>> dependencyGraph;

  factory DeveloperLock.fromJson(Map<String, Object?> json) {
    return DeveloperLock(
      schemaVersion: (json['schemaVersion'] as num?)?.toInt() ?? 0,
      projectId: (json['projectId'] as String?) ?? '',
      resolvedAtUtc: (json['resolvedAtUtc'] as String?) ?? '',
      packages: _lockedPackageList(json['packages']),
      dependencyGraph: _stringListMap(json['dependencyGraph']),
    );
  }

  Map<String, Object?> toJson() => {
    'schemaVersion': schemaVersion,
    'projectId': projectId,
    'resolvedAtUtc': resolvedAtUtc,
    'packages': packages.map((item) => item.toJson()).toList(),
    if (dependencyGraph.isNotEmpty) 'dependencyGraph': dependencyGraph,
  };
}

class DeveloperWorkspace {
  const DeveloperWorkspace({
    required this.projectRoot,
    this.project,
    this.lock,
    this.issues = const [],
    this.generatedPropsPath = '',
  });

  final String projectRoot;
  final DeveloperProject? project;
  final DeveloperLock? lock;
  final List<LauncherIssue> issues;
  final String generatedPropsPath;

  bool get hasProject => project != null;
  bool get hasBlockingIssues => issues.any((issue) => issue.isBlocking);
}

/// What kind of developer project a registry entry is. Drives which actions a project card offers (e.g. only
/// Unity projects get "Open in Unity").
enum ProjectKind { modCSharp, unityWorld, unityPackage, unknown }

/// Parses a persisted/serialized [ProjectKind] name (tolerant of legacy/unknown values).
ProjectKind projectKindFromString(String? value) {
  switch ((value ?? '').trim()) {
    case 'modCSharp':
      return ProjectKind.modCSharp;
    case 'unityWorld':
      return ProjectKind.unityWorld;
    case 'unityPackage':
      return ProjectKind.unityPackage;
    default:
      return ProjectKind.unknown;
  }
}

/// One tracked developer project in the VCC-style multi-project registry (persisted to
/// `developer_projects.json` at the launcher data root). The registry holds only metadata + a path; the project's
/// own files (`robotopia.project.json`, `Packages/vpm-manifest.json`, …) remain the source of truth.
class RegisteredProject {
  const RegisteredProject({
    required this.path,
    required this.name,
    this.kind = ProjectKind.unknown,
    this.unityVersion = '',
    this.lastOpenedUtc = '',
  });

  final String path;
  final String name;
  final ProjectKind kind;
  final String unityVersion;
  final String lastOpenedUtc;

  bool get isUnity =>
      kind == ProjectKind.unityWorld || kind == ProjectKind.unityPackage;

  RegisteredProject copyWith({
    String? name,
    ProjectKind? kind,
    String? unityVersion,
    String? lastOpenedUtc,
  }) {
    return RegisteredProject(
      path: path,
      name: name ?? this.name,
      kind: kind ?? this.kind,
      unityVersion: unityVersion ?? this.unityVersion,
      lastOpenedUtc: lastOpenedUtc ?? this.lastOpenedUtc,
    );
  }

  factory RegisteredProject.fromJson(Map<String, Object?> json) {
    return RegisteredProject(
      path: (json['path'] as String?) ?? '',
      name: (json['name'] as String?) ?? '',
      kind: projectKindFromString(json['kind'] as String?),
      unityVersion: (json['unityVersion'] as String?) ?? '',
      lastOpenedUtc: (json['lastOpenedUtc'] as String?) ?? '',
    );
  }

  Map<String, Object?> toJson() => {
    'path': path,
    'name': name,
    'kind': kind.name,
    if (unityVersion.isNotEmpty) 'unityVersion': unityVersion,
    if (lastOpenedUtc.isNotEmpty) 'lastOpenedUtc': lastOpenedUtc,
  };
}

/// An installed Unity editor discovered via Unity Hub (detect-only — the launcher never installs Unity).
class UnityEditor {
  const UnityEditor({required this.version, required this.path});

  final String version;
  final String path;

  factory UnityEditor.fromJson(Map<String, Object?> json) => UnityEditor(
    version: (json['version'] as String?) ?? '',
    path: (json['path'] as String?) ?? '',
  );

  Map<String, Object?> toJson() => {'version': version, 'path': path};
}
