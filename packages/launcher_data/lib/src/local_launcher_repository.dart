import 'dart:async';
import 'dart:convert';
import 'dart:ffi' as ffi;
import 'dart:io';
import 'dart:math';
import 'dart:typed_data';

import 'package:archive/archive.dart';
import 'package:crypto/crypto.dart';
import 'package:launcher_domain/launcher_domain.dart';
import 'package:path/path.dart' as p;
import 'package:unorm_dart/unorm_dart.dart' as unicode;

import 'bounded_process.dart';
import 'data_root.dart';
import 'dotnet_sdk.dart';
import 'game_install_discovery.dart';
import 'launch_running_probe.dart';
import 'launch_wine_configuration.dart';
import 'package_contract.dart';
import 'public_url.dart';
import 'secure_http.dart';
import 'safe_zip_archive.dart';
import 'launch_staging_store.dart';
import 'launch_storage_keys.dart';
import 'launch_activity_monitor.dart';
import 'launch_process_control.dart';

part 'local_launcher_repository/game_layout.dart';
part 'local_launcher_repository/game_install_discovery_helpers.dart';
part 'local_launcher_repository/game_architecture.dart';
part 'local_launcher_repository/diagnostics_helpers.dart';
part 'local_launcher_repository/game_runtime_helpers.dart';
part 'local_launcher_repository/manager_state_helpers.dart';
part 'local_launcher_repository/manager_state_validation.dart';
part 'local_launcher_repository/installed_package_validation.dart';
part 'local_launcher_repository/noncritical_logging.dart';
part 'local_launcher_repository/package_install_receipt.dart';
part 'local_launcher_repository/package_inbox.dart';
part 'local_launcher_repository/package_inbox_consumption.dart';
part 'local_launcher_repository/package_inbox_selection.dart';
part 'local_launcher_repository/package_installation_helpers.dart';
part 'local_launcher_repository/package_repair.dart';
part 'local_launcher_repository/package_metadata_validation.dart';
part 'local_launcher_repository/package_helpers.dart';
part 'local_launcher_repository/path_helpers.dart';
part 'local_launcher_repository/profile_launch_helpers.dart';
part 'local_launcher_repository/effective_launch_selection.dart';
part 'local_launcher_repository/launch_preview.dart';
part 'local_launcher_repository/launch_admission.dart';
part 'local_launcher_repository/profile_persistence.dart';
part 'local_launcher_repository/process_helpers.dart';
part 'local_launcher_repository/registry_source_helpers.dart';
part 'local_launcher_repository/registry_source_models.dart';
part 'local_launcher_repository/repository_hooks.dart';
part 'local_launcher_repository/runtime_transaction.dart';
part 'local_launcher_repository/runtime_repair_helpers.dart';
part 'local_launcher_repository/storage_helpers.dart';

class LocalLauncherRepository implements GameInstallDiscoveryRepository {
  LocalLauncherRepository({
    String? dataRoot,
    String? repositoryRoot,
    String? workingDirectory,
    String? knownGamePath,
    GameInstallDiscoveryService? gameInstallDiscoveryService,
    DependencyPlanner dependencyPlanner = const DependencyPlanner(),
    PackageMetadataValidator? packageMetadataValidator,
    PackageInstallCommitHook? packageInstallCommitHook,
    RuntimeRepairCommitHook? runtimeRepairCommitHook,
    GameProcessStarter? gameProcessStarter,
    GameProcessCreator? gameProcessCreator,
    GameRunningProbe? gameRunningProbe,
    GameProcessIdentityReader? gameProcessIdentityReader,
    GameProcessLiveness? gameProcessLiveness,
    GameProcessStopper? gameProcessStopper,
  }) : _dataRoot = Directory(dataRoot ?? resolveTopiaForgeDataRoot()),
       _repositoryRoot = Directory(
         repositoryRoot ?? _findRepositoryRoot(workingDirectory),
       ),
       _knownGamePath = knownGamePath,
       _gameInstallDiscovery =
           gameInstallDiscoveryService ??
           _defaultGameInstallDiscovery(knownGamePath),
       _dependencyPlanner = dependencyPlanner,
       _packageMetadataValidator = packageMetadataValidator,
       _packageInstallCommitHook = packageInstallCommitHook,
       _runtimeRepairCommitHook = runtimeRepairCommitHook,
       _gameProcessStarter = gameProcessStarter,
       _gameProcessCreator =
           gameProcessCreator ??
           (gameProcessStarter == null ? _startGameWithReceipt : null),
       _gameRunningProbe = gameRunningProbe ?? _defaultGameRunningProbe,
       _gameProcessIdentityReader =
           gameProcessIdentityReader ?? _unverifiedInjectedProcess,
       _gameProcessLiveness = gameProcessLiveness ?? isLaunchProcessAlive,
       _gameProcessStopper = gameProcessStopper ?? stopLaunchProcess {
    if (gameProcessCreator != null && gameProcessStarter != null) {
      throw ArgumentError(
        'Choose a creation receipt hook or a legacy process starter.',
      );
    }
  }
  final Directory _dataRoot;
  final Directory _repositoryRoot;
  final String? _knownGamePath;
  final GameInstallDiscoveryService _gameInstallDiscovery;
  final DependencyPlanner _dependencyPlanner;
  final PackageMetadataValidator? _packageMetadataValidator;
  final Map<String, Future<List<String>>> _installedMetadataCache = {};
  final PackageInstallCommitHook? _packageInstallCommitHook;
  final RuntimeRepairCommitHook? _runtimeRepairCommitHook;
  final GameProcessStarter? _gameProcessStarter;
  final GameProcessCreator? _gameProcessCreator;
  final GameRunningProbe _gameRunningProbe;
  final GameProcessIdentityReader _gameProcessIdentityReader;
  final GameProcessLiveness _gameProcessLiveness;
  final GameProcessStopper _gameProcessStopper;
  final Set<String> _launchAdmissions = {};
  final Map<String, LaunchProcessIdentity> _ownedLaunchProcesses = {};
  final Map<String, LaunchActivityMonitor> _launchMonitors = {};
  Future<void> _settingsMutationTail = Future<void>.value();
  Future<void> _launcherLogMutationTail = Future<void>.value();
  bool _disposed = false;
  final _launchActivities = StreamController<LaunchActivity>.broadcast();
  @override
  Stream<LaunchActivity> get launchActivities => _launchActivities.stream;
  @override
  Future<LaunchPreview> previewLaunch(
    GameInstall install,
    LauncherProfile profile, {
    LaunchSelection? selectionOverride,
  }) => _previewLaunch(install, profile, selectionOverride: selectionOverride);
  @override
  String get dataRoot => _dataRoot.path;
  File get _settingsFile => File(p.join(_dataRoot.path, 'settings.json'));
  File get _profilesFile => File(p.join(_dataRoot.path, 'profiles.json'));
  File get _sourcesFile => File(p.join(_dataRoot.path, 'package_sources.json'));
  File get _launcherLogFile =>
      File(p.join(_dataRoot.path, 'logs', 'launcher.log'));
  Directory get _packageCache =>
      Directory(p.join(_dataRoot.path, 'package-cache'));

  @override
  Future<LauncherSnapshot> loadSnapshot() async {
    _ensureDataRoot();
    final profiles = await _loadProfiles();
    final settings = await _loadSettings();
    final selectedProfileId =
        (settings['selectedProfileId'] as String?) ?? profiles.first.id;
    final discovery = await _resolveGameInstallDiscovery(settings);
    final gameInstallCandidates = discovery.candidates;
    final gameInstall = discovery.install;
    var installedMods = <InstalledMod>[];
    if (gameInstall != null) {
      try {
        installedMods = await _loadInstalledMods(gameInstall);
      } on _ManagerStateContentException {
        // Keep profiles and their explicit recovery previews available. Never
        // reconcile or replace manager state whose content cannot be trusted.
      }
    }
    final packageSources = await _loadPackageSources();
    final registryOutcome = await _loadRegistryOutcome(
      installedMods,
      packageSources,
    );
    final registryMods = registryOutcome.mods;
    final previews = <String, LaunchPreview>{};
    if (gameInstall != null) {
      for (final profile in profiles) {
        previews[profile.id] = await previewLaunch(gameInstall, profile);
      }
    }
    return LauncherSnapshot(
      gameInstall: gameInstall,
      gameInstallCandidates: gameInstallCandidates,
      profiles: profiles,
      selectedProfileId: selectedProfileId,
      installedMods: installedMods,
      registryMods: registryMods,
      packageSources: packageSources,
      previewsByProfile: previews,
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
  Future<List<GameInstallCandidate>> discoverGameInstalls() =>
      _discoverGameInstalls();

  @override
  Future<GameInstall?> detectKnownInstall() => _detectKnownInstall();

  @override
  Future<GameInstall> selectGameDirectory(String path) =>
      _selectGameDirectory(path);

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
    String sourceId = '',
  }) => _installPackage(
    packagePath,
    install,
    expectedSha256: expectedSha256,
    sourceId: sourceId,
  );

  @override
  Future<List<PackageSource>> savePackageSources(List<PackageSource> sources) =>
      _savePackageSources(sources);

  @override
  Future<PackageInboxInstallOutcome> installInboxPackages(
    GameInstall install,
  ) => _installInboxPackages(install);

  @override
  Future<List<InstalledMod>> repairInstalledMod(
    GameInstall install,
    InstalledMod mod,
  ) => _repairInstalledMod(install, mod);

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
    await _appendLauncherLogBestEffort(
      '${enabled ? 'Enabled' : 'Disabled'} $modId.',
    );
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
    await _appendLauncherLogBestEffort('Disabled all mods.');
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
    await _appendLauncherLogBestEffort('Uninstalled $modId.');
    return _loadInstalledMods(install);
  }

  @override
  Future<List<LauncherProfile>> saveProfiles(
    List<LauncherProfile> profiles,
    String selectedProfileId,
  ) => _saveVersionedProfiles(profiles, selectedProfileId);

  @override
  Future<void> exportProfile(LauncherProfile profile, String path) async {
    _requireProfileExportPath(path);
    _requireValidLauncherProfile(profile);
    await _writeJsonFileAtomic(
      File(path),
      {'schemaVersion': _profileFormatVersion, 'profile': profile.toJson()},
      maxBytes: _maxProfilesBytes,
      label: 'Exported launcher profile',
    );
  }

  @override
  Future<LauncherProfile> importProfile(String path) =>
      _importVersionedProfile(path);

  @override
  Future<LaunchResult> launch(
    GameInstall install,
    LauncherProfile profile, {
    LaunchSelection? selectionOverride,
  }) => _launchProfile(
    install,
    profile,
    selectionOverride: selectionOverride,
    restart: false,
  );

  @override
  Future<LaunchResult> restart(
    GameInstall install,
    LauncherProfile profile, {
    LaunchSelection? selectionOverride,
  }) => _launchProfile(
    install,
    profile,
    selectionOverride: selectionOverride,
    restart: true,
  );

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
    if (_disposed) return;
    _disposed = true;
    for (final monitor in _launchMonitors.values) {
      await monitor.dispose();
    }
    _launchMonitors.clear();
    await _launchActivities.close();
  }
}
