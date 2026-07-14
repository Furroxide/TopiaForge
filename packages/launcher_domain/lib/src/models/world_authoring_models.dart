part of '../models.dart';

/// Pairing config between a Unity world-authoring project and the mod that ships its bundle, stored as
/// `topiaforge.world.json` at the Unity project root. Build-time input only — the game runtime never reads
/// it (world identity/metadata are baked into the mod's C#).
class WorldAuthoringConfig {
  const WorldAuthoringConfig({
    this.schemaVersion = 2,
    this.worldId = '',
    this.bundleName = '',
    this.worldPrefab = defaultWorldPrefab,
    this.modPath = '',
  });

  static const String fileName = 'topiaforge.world.json';
  static const String defaultWorldPrefab = 'Assets/World/World.prefab';

  final int schemaVersion;

  /// The world id the paired mod registers (informational; keeps the pairing self-describing).
  final String worldId;

  /// AssetBundle name (lowercase); the build lands at `<mod>/AssetBundles/<bundleName>.bundle`.
  final String bundleName;

  /// Project-relative asset path of the world root prefab.
  final String worldPrefab;

  /// The paired mod directory: absolute, or relative to the Unity project root.
  final String modPath;

  factory WorldAuthoringConfig.fromJson(Map<String, Object?> json) =>
      WorldAuthoringConfig(
        schemaVersion: (json['schemaVersion'] as num?)?.toInt() ?? 0,
        worldId: (json['worldId'] as String?) ?? '',
        bundleName: (json['bundleName'] as String?) ?? '',
        worldPrefab: (json['worldPrefab'] as String?) ?? defaultWorldPrefab,
        modPath: (json['modPath'] as String?) ?? '',
      );

  Map<String, Object?> toJson() => {
    'schemaVersion': schemaVersion,
    'worldId': worldId,
    'bundleName': bundleName,
    'worldPrefab': worldPrefab,
    'modPath': modPath,
  };

  WorldAuthoringConfig copyWith({
    String? worldId,
    String? bundleName,
    String? worldPrefab,
    String? modPath,
  }) => WorldAuthoringConfig(
    schemaVersion: schemaVersion,
    worldId: worldId ?? this.worldId,
    bundleName: bundleName ?? this.bundleName,
    worldPrefab: worldPrefab ?? this.worldPrefab,
    modPath: modPath ?? this.modPath,
  );

  /// Derives a bundle name from a mod/world id: lowercase, non [a-z0-9-] runs collapsed to '-'.
  static String deriveBundleName(String id) {
    final lowered = id.toLowerCase();
    final cleaned = lowered
        .replaceAll(RegExp('[^a-z0-9-]+'), '-')
        .replaceAll(RegExp('-+'), '-')
        .replaceAll(RegExp(r'^-|-$'), '');
    return cleaned.isEmpty ? 'world' : cleaned;
  }
}

/// Unity editor compatibility for TopiaForge authoring. Code-only mods do not
/// require Unity, but Unity-authored assets/worlds must be serialized by the
/// same editor version as the shipped player.
class RobotopiaGameUnityCompatibility {
  static const requiredEditorVersion = '6000.0.23f1';
  static const requiredEditorChangeset = '1c4764c07fb4';
  static const requiredEditorDisplay =
      '$requiredEditorVersion ($requiredEditorChangeset)';
  static const installHint =
      'Install Unity $requiredEditorVersion via Unity Hub (Installs > '
      'Install Editor > Archive), or headless with the Hub CLI ("Unity Hub.exe" '
      'on Windows, unityhub on macOS/Linux): <hub> -- --headless install '
      '--version $requiredEditorVersion --changeset $requiredEditorChangeset';

  /// Selects only the exact editor requested by a project, or the TopiaForge
  /// release editor when the project has no explicit pin. The order returned
  /// by Unity Hub discovery must never change this compatibility decision.
  static UnityEditor? selectEditor(
    Iterable<UnityEditor> editors, {
    String configuredVersion = '',
  }) {
    final configured = configuredVersion.trim();
    final required = configured.isEmpty ? requiredEditorVersion : configured;
    for (final editor in editors) {
      if (editor.version.trim() == required) {
        return editor;
      }
    }
    return null;
  }
}

/// Pure editor-version gate for world/UI bundle builds. Enforced by
/// `topiaforge world build` and `topiaforge unity build-ui-bundle`.
class WorldBundleEditorGate {
  static bool isEligible(String version) =>
      version.trim() == RobotopiaGameUnityCompatibility.requiredEditorVersion;

  /// Remediation shown when no eligible editor exists.
  static const String installHint = RobotopiaGameUnityCompatibility.installHint;
}

/// Outcome of a headless world-bundle build.
class WorldBundleBuildResult {
  const WorldBundleBuildResult({
    required this.success,
    this.bundlePath = '',
    this.sha256 = '',
    this.sizeBytes = 0,
    this.editorPath = '',
    this.editorVersion = '',
    this.logPath = '',
    this.errorMessage = '',
    this.logTail = const <String>[],
  });

  final bool success;
  final String bundlePath;
  final String sha256;
  final int sizeBytes;
  final String editorPath;
  final String editorVersion;
  final String logPath;
  final String errorMessage;

  /// The last lines of the Unity log when the build failed (actionable context without opening the file).
  final List<String> logTail;
}
