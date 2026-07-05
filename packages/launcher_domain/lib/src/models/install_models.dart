part of '../models.dart';

class InstalledMod {
  const InstalledMod({
    required this.id,
    required this.name,
    required this.version,
    required this.enabled,
    required this.restartRequired,
    required this.uninstallPending,
    required this.packagePath,
    this.manifest,
    this.installedAtUtc = '',
    this.updatedAtUtc = '',
    this.errors = const [],
  });

  final String id;
  final String name;
  final String version;
  final bool enabled;
  final bool restartRequired;
  final bool uninstallPending;
  final String packagePath;
  final ModManifest? manifest;
  final String installedAtUtc;
  final String updatedAtUtc;
  final List<String> errors;

  bool get isValid => manifest != null && errors.isEmpty;

  InstalledMod copyWith({
    bool? enabled,
    bool? restartRequired,
    bool? uninstallPending,
    String? version,
  }) {
    return InstalledMod(
      id: id,
      name: name,
      version: version ?? this.version,
      enabled: enabled ?? this.enabled,
      restartRequired: restartRequired ?? this.restartRequired,
      uninstallPending: uninstallPending ?? this.uninstallPending,
      packagePath: packagePath,
      manifest: manifest,
      installedAtUtc: installedAtUtc,
      updatedAtUtc: DateTime.now().toUtc().toIso8601String(),
      errors: errors,
    );
  }
}

enum ComponentState { missing, partial, ready }

/// One mod feature whose reflection binding into the game changed in a way the GameCompat.Extractor detected.
/// Purely informational: compat findings live here, NEVER in [GameInstall.issues], so they can never make a game
/// un-launchable (WARN-ONLY behaviour).
class CompatFinding {
  const CompatFinding({
    required this.modId,
    required this.bindingId,
    required this.feature,
    required this.changeKind,
    required this.severity,
    this.detail = '',
  });

  final String modId;
  final String bindingId;
  final String feature;
  final String changeKind;
  final IssueSeverity severity;
  final String detail;

  Map<String, Object?> toJson() => {
    'modId': modId,
    'bindingId': bindingId,
    'feature': feature,
    'changeKind': changeKind,
    'severity': severity.name,
    if (detail.isNotEmpty) 'detail': detail,
  };

  factory CompatFinding.fromJson(Map<String, Object?> json) => CompatFinding(
    modId: (json['modId'] as String?) ?? '',
    bindingId: (json['bindingId'] as String?) ?? '',
    feature: (json['feature'] as String?) ?? '',
    changeKind: (json['changeKind'] as String?) ?? '',
    severity: _issueSeverityFrom(json['severity'] as String?),
    detail: (json['detail'] as String?) ?? '',
  );
}

/// The result of checking the installed game against the mods' declared reflection bindings.
/// [status] mirrors the extractor: `ok` (all critical bindings present), `broken` (a critical binding is gone),
/// `skipped` (no game install), `unknown` (the check could not run — e.g. the extractor tool is absent).
class GameCompatStatus {
  const GameCompatStatus({
    required this.status,
    this.gameVersionLabel = '',
    this.surfaceHash = '',
    this.gameCodeSha = '',
    this.findings = const [],
    this.extractorVersion = '',
  });

  final String status;
  final String gameVersionLabel;
  final String surfaceHash;
  final String gameCodeSha;
  final List<CompatFinding> findings;
  final String extractorVersion;

  bool get isKnown => status == 'ok' || status == 'broken';
  bool get hasError => findings.any((f) => f.severity == IssueSeverity.error);
  bool get hasWarning =>
      findings.any((f) => f.severity == IssueSeverity.warning);
  int get errorCount =>
      findings.where((f) => f.severity == IssueSeverity.error).length;

  factory GameCompatStatus.skipped() =>
      const GameCompatStatus(status: 'skipped');
  factory GameCompatStatus.unknown() =>
      const GameCompatStatus(status: 'unknown');

  Map<String, Object?> toJson() => {
    'status': status,
    'gameVersionLabel': gameVersionLabel,
    'surfaceHash': surfaceHash,
    'gameCodeSha': gameCodeSha,
    'extractorVersion': extractorVersion,
    'findings': findings.map((f) => f.toJson()).toList(),
  };

  factory GameCompatStatus.fromJson(Map<String, Object?> json) =>
      GameCompatStatus(
        status: (json['status'] as String?) ?? 'unknown',
        gameVersionLabel: (json['gameVersionLabel'] as String?) ?? '',
        surfaceHash: (json['surfaceHash'] as String?) ?? '',
        gameCodeSha: (json['gameCodeSha'] as String?) ?? '',
        extractorVersion: (json['extractorVersion'] as String?) ?? '',
        findings: ((json['findings'] as List<Object?>?) ?? const [])
            .whereType<Map<String, Object?>>()
            .map(CompatFinding.fromJson)
            .toList(),
      );
}

// The extractor prints severities Title-cased (Info/Warning/Error); map to the domain enum.
IssueSeverity _issueSeverityFrom(String? name) => switch (name) {
  'Error' || 'error' => IssueSeverity.error,
  'Warning' || 'warning' => IssueSeverity.warning,
  _ => IssueSeverity.info,
};

/// How the game is laid out on disk, which drives executable/managed paths,
/// the BepInEx flavour to install, and how launch/restart behave.
///
/// - [windowsNative]: Robotopia.exe in the game folder on a Windows host.
/// - [macAppBundle]: Robotopia.app bundle on macOS (BepInEx unix build,
///   doorstop injected via DYLD environment variables).
/// - [linuxProton]: the Windows build selected on a non-Windows host — a
///   Proton/Wine install. Uses the Windows BepInEx; the game must run with
///   WINEDLLOVERRIDES="winhttp=n,b".
enum GameInstallLayout { windowsNative, macAppBundle, linuxProton }

class GameInstall {
  const GameInstall({
    required this.path,
    required this.executablePath,
    required this.bepInExStatus,
    required this.loaderStatus,
    this.layout = GameInstallLayout.windowsNative,
    this.issues = const [],
    this.compatStatus,
  });

  final String path;
  final String executablePath;
  final ComponentState bepInExStatus;
  final ComponentState loaderStatus;
  final GameInstallLayout layout;
  final List<LauncherIssue> issues;

  /// Informational game-compatibility status. Deliberately separate from [issues] so it can never affect
  /// [canLaunch] — a broken mod binding warns the player, it never blocks the game.
  final GameCompatStatus? compatStatus;

  bool get canLaunch => issues.every((issue) => !issue.isBlocking);
  bool get needsRepair =>
      bepInExStatus != ComponentState.ready ||
      loaderStatus != ComponentState.ready;

  GameInstall copyWith({GameCompatStatus? compatStatus}) => GameInstall(
    path: path,
    executablePath: executablePath,
    bepInExStatus: bepInExStatus,
    loaderStatus: loaderStatus,
    layout: layout,
    issues: issues,
    compatStatus: compatStatus ?? this.compatStatus,
  );
}

class RegistryMod {
  const RegistryMod({
    required this.manifest,
    this.downloadUrl = '',
    this.packageSha256 = '',
    this.changelog = '',
    this.sourceId = '',
    this.sourceName = '',
    this.installedVersion,
  });

  final ModManifest manifest;
  final String downloadUrl;
  final String packageSha256;
  final String changelog;
  final String sourceId;
  final String sourceName;
  final String? installedVersion;

  bool get isInstalled => installedVersion != null;

  bool get updateAvailable {
    final installed = SemanticVersion.tryParse(installedVersion);
    final available = SemanticVersion.tryParse(manifest.version);
    if (installed == null || available == null) {
      return false;
    }
    return available.compareTo(installed) > 0;
  }

  factory RegistryMod.fromJson(Map<String, Object?> json) {
    return RegistryMod(
      manifest: ModManifest.fromJson(_objectMap(json['manifest'])),
      downloadUrl: (json['downloadUrl'] as String?) ?? '',
      packageSha256: (json['packageSha256'] as String?) ?? '',
      changelog: (json['changelog'] as String?) ?? '',
      sourceId: (json['sourceId'] as String?) ?? '',
      sourceName: (json['sourceName'] as String?) ?? '',
    );
  }
}

class LegacyMod {
  const LegacyMod({
    required this.id,
    required this.name,
    required this.path,
    required this.kind,
    this.canMigrate = false,
    this.details = '',
  });

  final String id;
  final String name;
  final String path;
  final String kind;
  final bool canMigrate;
  final String details;
}

class RepairReport {
  const RepairReport({required this.actions, required this.issues});

  final List<String> actions;
  final List<LauncherIssue> issues;

  bool get ok => issues.every((issue) => !issue.isBlocking);
}

class LaunchResult {
  const LaunchResult({
    required this.started,
    required this.message,
    this.processId,
  });

  final bool started;
  final String message;
  final int? processId;
}

class LauncherSnapshot {
  const LauncherSnapshot({
    required this.profiles,
    required this.selectedProfileId,
    required this.installedMods,
    required this.registryMods,
    required this.packageSources,
    required this.worldCatalog,
    required this.legacyMods,
    required this.recentLog,
    this.gameInstall,
    this.launcherUpdates = const LauncherUpdateSettings(),
    this.developerMode = false,
    this.sourceStatuses = const [],
    this.launcherLog = '',
  });

  final GameInstall? gameInstall;
  final List<LauncherProfile> profiles;
  final String selectedProfileId;
  final List<InstalledMod> installedMods;
  final List<RegistryMod> registryMods;
  final List<PackageSource> packageSources;
  final WorldCatalog worldCatalog;
  final List<LegacyMod> legacyMods;
  final String recentLog;
  final LauncherUpdateSettings launcherUpdates;

  /// Opt-in developer mode. Off by default so the launcher is a clean install-and-play app for the majority of
  /// users, who never build a mod. When on, the Developer tab (project tools, UGC live-sync) is revealed.
  final bool developerMode;

  /// Per-source load health from the most recent catalog load (enabled
  /// sources only). Empty until the repository wires it through.
  final List<PackageSourceStatus> sourceStatuses;

  /// Tail of the launcher's own log, for in-app diagnostics.
  final String launcherLog;
}
