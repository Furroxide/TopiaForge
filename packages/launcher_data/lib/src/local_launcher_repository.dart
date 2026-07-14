import 'dart:async';
import 'dart:convert';
import 'dart:io';
import 'dart:typed_data';

import 'package:archive/archive.dart';
import 'package:crypto/crypto.dart';
import 'package:launcher_domain/launcher_domain.dart';
import 'package:path/path.dart' as p;

import 'bounded_process.dart';
import 'data_root.dart';
import 'process_identity.dart';
import 'public_url.dart';
import 'secure_http.dart';
import 'ugc_sidecar_runtime.dart';
import 'safe_zip_archive.dart';

part 'local_launcher_repository/game_layout.dart';
part 'local_launcher_repository/game_runtime_helpers.dart';
part 'local_launcher_repository/legacy_diagnostics_helpers.dart';
part 'local_launcher_repository/manager_state_helpers.dart';
part 'local_launcher_repository/package_installation_helpers.dart';
part 'local_launcher_repository/package_helpers.dart';
part 'local_launcher_repository/path_helpers.dart';
part 'local_launcher_repository/profile_launch_helpers.dart';
part 'local_launcher_repository/process_helpers.dart';
part 'local_launcher_repository/registry_source_helpers.dart';
part 'local_launcher_repository/repository_hooks.dart';
part 'local_launcher_repository/runtime_transaction.dart';
part 'local_launcher_repository/runtime_repair_helpers.dart';
part 'local_launcher_repository/storage_helpers.dart';
part 'local_launcher_repository/ugc_live_sync_helpers.dart';
part 'local_launcher_repository/ugc_publisher_helpers.dart';

class LocalLauncherRepository implements LauncherRepository {
  LocalLauncherRepository({
    String? dataRoot,
    String? repositoryRoot,
    String? workingDirectory,
    String? knownGamePath,
    DependencyPlanner dependencyPlanner = const DependencyPlanner(),
    PackageInstallCommitHook? packageInstallCommitHook,
    RuntimeRepairCommitHook? runtimeRepairCommitHook,
    UgcInspectionReadHook? ugcInspectionReadHook,
    GameProcessStarter? gameProcessStarter,
  }) : _dataRoot = Directory(dataRoot ?? resolveRobotopiaDataRoot()),
       _repositoryRoot = Directory(
         repositoryRoot ?? _findRepositoryRoot(workingDirectory),
       ),
       _knownGamePath = knownGamePath,
       _dependencyPlanner = dependencyPlanner,
       _packageInstallCommitHook = packageInstallCommitHook,
       _runtimeRepairCommitHook = runtimeRepairCommitHook,
       _ugcInspectionReadHook = ugcInspectionReadHook,
       _gameProcessStarter = gameProcessStarter ?? _startDetachedGameProcess;

  final Directory _dataRoot;
  final Directory _repositoryRoot;
  final String? _knownGamePath;
  final DependencyPlanner _dependencyPlanner;
  final PackageInstallCommitHook? _packageInstallCommitHook;
  final RuntimeRepairCommitHook? _runtimeRepairCommitHook;
  final UgcInspectionReadHook? _ugcInspectionReadHook;
  final GameProcessStarter _gameProcessStarter;
  Future<void> _settingsMutationTail = Future<void>.value();
  Future<void> _launcherLogMutationTail = Future<void>.value();
  final StreamController<UgcPublisherEvent> _ugcPublisherEvents =
      StreamController<UgcPublisherEvent>.broadcast();
  Process? _ugcPublisher;
  StreamSubscription<String>? _ugcPublisherStdout;
  StreamSubscription<String>? _ugcPublisherStderr;
  int _ugcPublisherSessionId = 0;
  bool _ugcPublisherStopping = false;
  bool _disposed = false;

  static const _bepInExVersion = '5.4.23.5';
  static const _loaderVersion = RobotopiaRuntimeVersions.loaderVersion;
  static const _sdkVersion = RobotopiaRuntimeVersions.sdkVersion;
  @override
  String get dataRoot => _dataRoot.path;

  File get _settingsFile => File(p.join(_dataRoot.path, 'settings.json'));
  File get _profilesFile => File(p.join(_dataRoot.path, 'profiles.json'));
  File get _sourcesFile => File(p.join(_dataRoot.path, 'package_sources.json'));
  File get _launcherLogFile =>
      File(p.join(_dataRoot.path, 'logs', 'launcher.log'));
  File get _ugcPublisherSessionFile =>
      File(p.join(_dataRoot.path, 'ugc-session.json'));
  Directory get _packageCache =>
      Directory(p.join(_dataRoot.path, 'package-cache'));

  @override
  Future<LauncherSnapshot> loadSnapshot() async {
    _ensureDataRoot();
    final profiles = await _loadProfiles();
    final settings = await _loadSettings();
    final selectedProfileId =
        (settings['selectedProfileId'] as String?) ?? profiles.first.id;
    final configuredPath = settings['gamePath'] as String?;
    final gameInstall =
        configuredPath != null && configuredPath.trim().isNotEmpty
        ? await _validateGameDirectory(configuredPath)
        : await detectKnownInstall();
    final installedMods = gameInstall == null
        ? <InstalledMod>[]
        : await _loadInstalledMods(gameInstall);
    final packageSources = await _loadPackageSources();
    final registryOutcome = await _loadRegistryOutcome(
      installedMods,
      packageSources,
    );
    final registryMods = registryOutcome.mods;
    return LauncherSnapshot(
      gameInstall: gameInstall,
      profiles: profiles,
      selectedProfileId: selectedProfileId,
      installedMods: installedMods,
      registryMods: registryMods,
      packageSources: packageSources,
      worldCatalog: gameInstall == null
          ? WorldCatalog.fallback()
          : await _loadWorldCatalog(gameInstall, installedMods, registryMods),
      legacyMods: gameInstall == null
          ? <LegacyMod>[]
          : await detectLegacyMods(gameInstall),
      recentLog: gameInstall == null
          ? await _readLauncherLog()
          : await readRecentLog(gameInstall),
      launcherUpdates: LauncherUpdateSettings.fromJson(
        _objectMap(settings['launcherUpdates']),
      ),
      developerMode: (settings['developerMode'] as bool?) ?? false,
      sourceStatuses: registryOutcome.statuses,
      launcherLog: await _readLauncherLog(),
    );
  }

  @override
  Future<void> setDeveloperMode(bool enabled) async {
    await _updateSettings((settings) => settings['developerMode'] = enabled);
  }

  @override
  Future<void> saveLauncherUpdateSettings(
    LauncherUpdateSettings settings,
  ) async {
    final trustedSettings = LauncherUpdateSettings.fromJson(settings.toJson());
    await _updateSettings(
      (persisted) => persisted['launcherUpdates'] = trustedSettings.toJson(),
    );
  }

  @override
  Future<GameInstall?> detectKnownInstall() async {
    final knownPath = _knownGamePath ?? _defaultKnownGamePath();
    if (knownPath == null || GameLayout.resolve(knownPath) == null) {
      return null;
    }
    return _validateGameDirectory(knownPath);
  }

  @override
  Future<GameInstall> selectGameDirectory(String path) async {
    final install = await _validateGameDirectory(path);
    if (install.issues.any((issue) => issue.isBlocking)) {
      throw StateError(install.issues.map((issue) => issue.message).join(' '));
    }
    await _updateSettings((settings) => settings['gamePath'] = install.path);
    await _appendLauncherLog('Selected game directory ${install.path}.');
    return install;
  }

  @override
  Future<GameCompatStatus> checkGameCompat(GameInstall install) async {
    final layout = GameLayout.resolve(install.path);
    if (layout == null) {
      return GameCompatStatus.skipped();
    }
    return _checkGameCompat(
      Directory(layout.gameRoot),
      Directory(layout.managedDirPath),
      force: true,
    );
  }

  @override
  Future<RepairReport> installOrRepairRuntime(GameInstall install) =>
      _installOrRepairRuntime(install);

  @override
  Future<PackageInstallPlan> previewPackage(
    String packagePath,
    GameInstall install, {
    String expectedSha256 = '',
    String sourceId = '',
    String sourceName = '',
  }) async {
    return _previewPackageInstallPlan(
      packagePath,
      install,
      expectedSha256: expectedSha256,
      sourceId: sourceId,
      sourceName: sourceName,
    );
  }

  @override
  Future<List<InstalledMod>> installPackage(
    String packagePath,
    GameInstall install, {
    String expectedSha256 = '',
  }) => _installPackage(packagePath, install, expectedSha256: expectedSha256);

  @override
  Future<List<PackageSource>> savePackageSources(
    List<PackageSource> sources,
  ) async {
    final normalized = sources.isEmpty ? _defaultPackageSources() : sources;
    _validatePackageSources(normalized);
    await _writeJsonFileAtomic(
      _sourcesFile,
      {'sources': normalized.map((source) => source.toJson()).toList()},
      maxBytes: _maxPackageSourcesBytes,
      label: 'Package sources',
    );
    await _appendLauncherLog('Saved ${normalized.length} package sources.');
    return normalized;
  }

  @override
  Future<List<InstalledMod>> installInboxPackages(GameInstall install) async {
    final inbox = _packageInbox(install);
    if (!inbox.existsSync()) {
      return _loadInstalledMods(install);
    }

    for (final file in inbox.listSync().whereType<File>().where(
      (file) => file.path.toLowerCase().endsWith('.robotopiamod'),
    )) {
      try {
        await installPackage(file.path, install);
      } on Object catch (error) {
        await _appendLauncherLog(
          'Inbox install failed for ${file.path}: $error',
        );
      }
    }

    return _loadInstalledMods(install);
  }

  @override
  Future<List<InstalledMod>> setModEnabled(
    GameInstall install,
    String modId,
    bool enabled,
  ) async {
    _requireSafeModId(modId);
    final state = await _readManagerState(install);
    for (final item in (state['mods'] as List).whereType<Map>()) {
      if ((item['id'] as String?)?.toLowerCase() == modId.toLowerCase()) {
        item['enabled'] = enabled;
        item['restartRequired'] = true;
        item['updatedAtUtc'] = DateTime.now().toUtc().toIso8601String();
      }
    }
    await _saveManagerState(install, state);
    await _appendLauncherLog('${enabled ? 'Enabled' : 'Disabled'} $modId.');
    return _loadInstalledMods(install);
  }

  @override
  Future<List<InstalledMod>> disableAllMods(GameInstall install) async {
    final state = await _readManagerState(install);
    for (final item in (state['mods'] as List).whereType<Map>()) {
      item['enabled'] = false;
      item['restartRequired'] = true;
      item['updatedAtUtc'] = DateTime.now().toUtc().toIso8601String();
    }
    await _saveManagerState(install, state);
    await _appendLauncherLog('Disabled all mods.');
    return _loadInstalledMods(install);
  }

  @override
  Future<List<InstalledMod>> uninstallMod(
    GameInstall install,
    String modId,
  ) async {
    _requireSafeModId(modId);
    final modRoot = Directory(p.join(_packagesRoot(install).path, modId));
    if (modRoot.existsSync()) {
      modRoot.deleteSync(recursive: true);
    }

    final state = await _readManagerState(install);
    final mods = (state['mods'] as List).whereType<Map>().toList();
    mods.removeWhere(
      (item) => (item['id'] as String?)?.toLowerCase() == modId.toLowerCase(),
    );
    state['mods'] = mods;
    await _saveManagerState(install, state);
    await _appendLauncherLog('Uninstalled $modId.');
    return _loadInstalledMods(install);
  }

  @override
  Future<List<LauncherProfile>> saveProfiles(
    List<LauncherProfile> profiles,
    String selectedProfileId,
  ) async {
    final normalizedProfiles = profiles.isEmpty
        ? [LauncherProfile.defaultProfile()]
        : profiles;
    await _writeJsonFileAtomic(
      _profilesFile,
      {
        'profiles': normalizedProfiles
            .map((profile) => profile.toJson())
            .toList(),
      },
      maxBytes: _maxProfilesBytes,
      label: 'Launcher profiles',
    );
    await _updateSettings(
      (settings) => settings['selectedProfileId'] = selectedProfileId,
    );
    return normalizedProfiles;
  }

  @override
  Future<void> exportProfile(LauncherProfile profile, String path) async {
    await _writeJsonFileAtomic(
      File(path),
      profile.toJson(),
      maxBytes: _maxProfilesBytes,
      label: 'Exported launcher profile',
    );
  }

  @override
  Future<LauncherProfile> importProfile(String path) async {
    final decoded = await _readJsonFileBounded(
      File(path),
      maxBytes: _maxProfilesBytes,
      label: 'Imported launcher profile',
    );
    if (decoded is! Map) {
      throw const FormatException(
        'Imported launcher profile is not an object.',
      );
    }
    return LauncherProfile.fromJson(
      decoded.map((key, value) => MapEntry(key.toString(), value)),
    );
  }

  @override
  Future<LaunchResult> launch(
    GameInstall install,
    LauncherProfile profile,
  ) async {
    final prepared = await _prepareRuntimeForLaunch(install);
    if (prepared.failure != null) {
      return prepared.failure!;
    }
    final launchInstall = prepared.install!;
    final message = profile.launchSettings.safeMode
        ? 'Launched Robotopia in safe mode for this run only.'
        : 'Launched Robotopia.';
    return _startGameWithWorldSelection(
      launchInstall,
      profile,
      message: message,
    );
  }

  @override
  Future<LaunchResult> restart(
    GameInstall install,
    LauncherProfile profile,
  ) async {
    final stopped = await _stopGameIfRunning(install);
    final prepared = await _prepareRuntimeForLaunch(install);
    if (prepared.failure != null) {
      return prepared.failure!;
    }
    final launchInstall = prepared.install!;
    final message = switch ((stopped, profile.launchSettings.safeMode)) {
      (true, true) => 'Restarted Robotopia in safe mode for this run only.',
      (true, false) => 'Restarted Robotopia.',
      (false, true) =>
        'Started Robotopia in temporary safe mode. No running process was found.',
      (false, false) => 'Started Robotopia. No running process was found.',
    };
    return _startGameWithWorldSelection(
      launchInstall,
      profile,
      message: message,
    );
  }

  @override
  Future<List<LegacyMod>> detectLegacyMods(GameInstall install) =>
      _detectLegacyMods(install);

  @override
  Future<String> deployUgcLiveSyncConfig(
    GameInstall install,
    UgcLiveSyncSettings settings,
  ) => _deployUgcLiveSyncConfig(install, settings);

  @override
  Future<UgcLiveSyncCleanupReport> cleanupUgcLiveSync(
    GameInstall install,
    UgcLiveSyncSettings fallbackSettings,
  ) => _cleanupUgcLiveSync(install, fallbackSettings);

  @override
  Stream<UgcPublisherEvent> get ugcPublisherEvents =>
      _ugcPublisherEvents.stream;

  @override
  bool get isUgcPublisherRunning => _ugcPublisher != null;

  @override
  Future<UgcPublisherStartResult> startUgcPublisher(
    UgcLiveSyncSettings settings,
  ) => _startUgcPublisher(settings);

  @override
  Future<void> stopUgcPublisher({bool waitForExit = false}) =>
      _stopUgcPublisher(waitForExit: waitForExit);

  @override
  Future<void> revokeUgcPublisherSession() async {
    await _deleteFileIfExists(_ugcPublisherSessionFile);
  }

  @override
  Future<UgcLiveSyncStatusSnapshot?> readUgcLiveSyncStatus(
    GameInstall install,
  ) => _readUgcLiveSyncStatus(install);

  @override
  Future<UgcSceneInspectionResult> inspectWatchFolderScenes(
    String watchFolder,
  ) => _inspectWatchFolderScenes(watchFolder);

  @override
  Future<List<UgcSceneRef>> listWatchFolderScenes(String watchFolder) =>
      _listWatchFolderScenes(watchFolder);

  @override
  Future<DiagnosticBundle> createDiagnosticBundle(
    GameInstall install,
    DependencyResolutionResult resolution,
  ) => _createDiagnosticBundle(install, resolution);

  @override
  Future<String> readRecentLog(GameInstall install, {int maxLines = 200}) =>
      _readRecentCombinedLog(install, maxLines: maxLines);

  @override
  Future<void> openPath(String path) => _openPath(path);

  @override
  Future<void> openContainingFolder(String path) =>
      _openPath(File(path).absolute.parent.path);

  @override
  Future<void> ensureDirectory(String path) async {
    await Directory(path).create(recursive: true);
  }

  @override
  Future<void> dispose() async {
    if (_disposed) {
      return;
    }
    _disposed = true;
    try {
      await _stopUgcPublisher(waitForExit: true);
    } finally {
      await _cancelUgcPublisherOutput();
      if (!_ugcPublisherEvents.isClosed) {
        await _ugcPublisherEvents.close();
      }
    }
  }
}
