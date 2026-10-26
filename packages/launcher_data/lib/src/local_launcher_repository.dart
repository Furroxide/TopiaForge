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
import 'acceptance_isolation_context.dart';
import 'acceptance_isolation_identity.dart';
import 'acceptance_isolation_bootstrap.dart';
import 'tomato_cake_launcher_state.dart';
import 'multiplayer/multiplayer_repository.dart'
    show LocalMultiplayerRepository;
import 'multiplayer/multiplayer_store.dart'
    show ServerContentLoad, ServerContentStore;
import 'atomic_file_writes.dart';
import 'atomic_files.dart';

part 'local_launcher_repository/game_layout.dart';
part 'local_launcher_repository/game_install_discovery_helpers.dart';
part 'local_launcher_repository/game_architecture.dart';
part 'local_launcher_repository/diagnostics_helpers.dart';
part 'local_launcher_repository/game_runtime_helpers.dart';
part 'local_launcher_repository/manager_state_helpers.dart';
part 'local_launcher_repository/manager_state_validation.dart';
part 'local_launcher_repository/mod_state_mutations.dart';
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
part 'local_launcher_repository/owned_game_quit.dart';
part 'local_launcher_repository/acceptance_launch_helpers.dart';
part 'local_launcher_repository/registry_source_helpers.dart';
part 'local_launcher_repository/registry_source_models.dart';
part 'local_launcher_repository/registry_source_parsing.dart';
part 'local_launcher_repository/exact_package_catalog_loading.dart';
part 'local_launcher_repository/repository_hooks.dart';
part 'local_launcher_repository/repository_work.dart';
part 'local_launcher_repository/snapshot_loading.dart';
part 'local_launcher_repository/runtime_transaction.dart';
part 'local_launcher_repository/runtime_repair_helpers.dart';
part 'local_launcher_repository/storage_helpers.dart';
part 'local_launcher_repository/multiplayer_content_host.dart';
part 'local_launcher_repository/join_content_downloads.dart';
part 'local_launcher_repository/join_host_downloads.dart';
part 'local_launcher_repository/join_content_install.dart';
part 'local_launcher_repository/join_install_journal.dart';
part 'local_launcher_repository/join_install_preflight.dart';
part 'local_launcher_repository/join_server_profile.dart';
part 'local_launcher_repository/join_session_launch.dart';
part 'local_launcher_repository/join_session_handoffs.dart';
part 'local_launcher_repository/server_content_view.dart';
part 'local_launcher_repository/server_content_cleanup.dart';
part 'local_launcher_repository/server_content_judging.dart';

class LocalLauncherRepository
    with _MultiplayerContentHosting, _JoinSessionHosting
    implements GameInstallDiscoveryRepository {
  LocalLauncherRepository({
    String? dataRoot,
    AcceptanceIsolationContext? acceptanceIsolation,
    String? acceptanceChallenge,
    String? repositoryRoot,
    String? workingDirectory,
    String? knownGamePath,
    GameInstallDiscoveryService? gameInstallDiscoveryService,
    TomatoCakeLauncherState? tomatoCakeLauncherState,
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
    JoinInstallHook? joinInstallHook,
    OwnedGameQuitPolicy ownedGameQuit = const OwnedGameQuitPolicy(),
  }) : _joinInstallHook = joinInstallHook,
       _acceptanceIsolation = acceptanceIsolation,
       _acceptanceChallenge = acceptanceChallenge,
       _dataRoot = Directory(dataRoot ?? resolveTopiaForgeDataRoot()),
       _repositoryRoot = Directory(
         repositoryRoot ?? _findRepositoryRoot(workingDirectory),
       ),
       _knownGamePath = knownGamePath,
       _gameInstallDiscovery =
           gameInstallDiscoveryService ??
           _defaultGameInstallDiscovery(knownGamePath),
       _tomatoCakeLauncher =
           tomatoCakeLauncherState ?? TomatoCakeLauncherState(),
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
       _gameProcessStopper = gameProcessStopper ?? stopLaunchProcess,
       _ownedGameQuit = ownedGameQuit {
    if (acceptanceIsolation == null && acceptanceChallenge != null) {
      throw ArgumentError('Acceptance requires both isolation and challenge.');
    }
    if (acceptanceIsolation != null) {
      acceptanceIsolation.verify();
      if (!sameAcceptancePath(
        _dataRoot.path,
        acceptanceIsolation.launcherRoot,
      )) {
        throw ArgumentError(
          'Acceptance launcher data root must match the admitted layout.',
        );
      }
    }
    if (gameProcessCreator != null && gameProcessStarter != null) {
      throw ArgumentError(
        'Choose a creation receipt hook or a legacy process starter.',
      );
    }
  }
  final JoinInstallHook? _joinInstallHook;
  final AcceptanceIsolationContext? _acceptanceIsolation;
  final String? _acceptanceChallenge;
  final Map<String, AcceptanceIsolationRequest> _acceptanceRequests = {};
  final Map<String, LaunchProcessIdentity> _acceptanceReceipts = {};
  final Directory _dataRoot;
  final Directory _repositoryRoot;
  final String? _knownGamePath;
  final GameInstallDiscoveryService _gameInstallDiscovery;
  final TomatoCakeLauncherState _tomatoCakeLauncher;
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
  final OwnedGameQuitPolicy _ownedGameQuit;
  final Set<String> _launchAdmissions = {};
  final Map<String, LaunchProcessIdentity> _ownedLaunchProcesses = {};

  /// The launch request each owned process was started with, by the very
  /// receipt object [_ownedLaunchProcesses] holds.
  final Map<LaunchProcessIdentity, String> _ownedLaunchRequests =
      Map.identity();
  final Map<String, LaunchActivityMonitor> _launchMonitors = {};
  Future<void> _settingsMutationTail = Future<void>.value();
  Future<void> _launcherLogMutationTail = Future<void>.value();
  bool _launcherLogSwept = false;
  bool _disposed = false;
  Future<void>? _disposal;
  final Set<Future<Object?>> _inFlight = {};

  /// Cancelled as the close begins, to stop the work it should not wait for:
  /// a join's downloads ([_close]).
  final CancellationToken _closing = CancellationToken();
  final _launchActivities = StreamController<LaunchActivity>.broadcast();
  @override
  Stream<LaunchActivity> get launchActivities => _launchActivities.stream;
  @override
  Future<LaunchPreview> previewLaunch(
    GameInstall install,
    LauncherProfile profile, {
    LaunchSelection? selectionOverride,
  }) => _tracked(
    () =>
        _previewLaunch(install, profile, selectionOverride: selectionOverride),
  );
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
  Future<LauncherSnapshot> loadSnapshot() => _tracked(_loadSnapshot);

  @override
  Future<void> setDeveloperMode(bool enabled) => _tracked(
    () => _updateSettings((settings) => settings['developerMode'] = enabled),
  );

  @override
  Future<void> saveLauncherUpdateSettings(LauncherUpdateSettings settings) =>
      _tracked(() async {
        final trustedSettings = LauncherUpdateSettings.fromJson(
          settings.toJson(),
        );
        await _updateSettings(
          (persisted) =>
              persisted['launcherUpdates'] = trustedSettings.toJson(),
        );
      });

  @override
  Future<List<GameInstallCandidate>> discoverGameInstalls() =>
      _tracked(_discoverGameInstalls);

  @override
  Future<GameInstall?> detectKnownInstall() => _tracked(_detectKnownInstall);

  @override
  Future<GameInstall> selectGameDirectory(String path) =>
      _tracked(() => _selectGameDirectory(path));

  @override
  Future<GameCompatStatus> checkGameCompat(GameInstall install) =>
      _tracked(() async {
        final layout = GameLayout.resolve(install.path);
        if (layout == null) {
          return GameCompatStatus.skipped();
        }
        return _checkGameCompat(
          Directory(layout.gameRoot),
          Directory(layout.managedDirPath),
          force: true,
        );
      });

  @override
  Future<RepairReport> installOrRepairRuntime(GameInstall install) =>
      _tracked(() => _installOrRepairRuntime(install));

  @override
  Future<PackageInstallPlan> previewPackage(
    String packagePath,
    GameInstall install, {
    String expectedSha256 = '',
    String sourceId = '',
    String sourceName = '',
  }) => _tracked(
    () => _previewPackageInstallPlan(
      packagePath,
      install,
      expectedSha256: expectedSha256,
      sourceId: sourceId,
      sourceName: sourceName,
    ),
  );

  @override
  Future<List<InstalledMod>> installPackage(
    String packagePath,
    GameInstall install, {
    String expectedSha256 = '',
    String sourceId = '',
  }) => _tracked(
    () => _installPackage(
      packagePath,
      install,
      expectedSha256: expectedSha256,
      sourceId: sourceId,
    ),
  );

  @override
  Future<List<PackageSource>> savePackageSources(List<PackageSource> sources) =>
      _tracked(() => _savePackageSources(sources));

  @override
  Future<PackageInboxInstallOutcome> installInboxPackages(
    GameInstall install,
  ) => _tracked(() => _installInboxPackages(install));

  @override
  Future<List<InstalledMod>> repairInstalledMod(
    GameInstall install,
    InstalledMod mod,
  ) => _tracked(() => _repairInstalledMod(install, mod));

  @override
  Future<List<InstalledMod>> setModEnabled(
    GameInstall install,
    String modId,
    bool enabled,
  ) => _tracked(() => _setModEnabled(install, modId, enabled));

  @override
  Future<List<InstalledMod>> disableAllMods(GameInstall install) =>
      _tracked(() => _disableAllMods(install));

  @override
  Future<List<InstalledMod>> uninstallMod(GameInstall install, String modId) =>
      _tracked(() => _uninstallMod(install, modId));

  @override
  Future<List<LauncherProfile>> saveProfiles(
    List<LauncherProfile> profiles,
    String selectedProfileId,
  ) => _tracked(() => _saveVersionedProfiles(profiles, selectedProfileId));

  @override
  Future<void> exportProfile(LauncherProfile profile, String path) =>
      _tracked(() async {
        _requireProfileExportPath(path);
        _requireValidLauncherProfile(profile);
        await _writeJsonFileAtomic(
          File(path),
          {'schemaVersion': _profileFormatVersion, 'profile': profile.toJson()},
          maxBytes: _maxProfilesBytes,
          label: 'Exported launcher profile',
        );
      });

  @override
  Future<LauncherProfile> importProfile(String path) =>
      _tracked(() => _importVersionedProfile(path));

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

  /// Starts [profile] to host or join [session], staging launch request V5.
  /// [launch] and [restart] keep staging V4 for every other launch.
  ///
  /// A join always starts at the main menu: the host's session decides what
  /// loads. [restart] replaces a running game this launcher owns, as the
  /// join flow does to restart into a server's profile.
  ///
  /// A launch V5 cannot carry is refused before anything happens, so a
  /// restart never stops the running game for it: Safe Mode, a join from a
  /// profile that inherits the manager's mod state, and acceptance runs.
  ///
  /// Not on [LauncherRepository] yet: the multiplayer slices consume the
  /// concrete type, and widening the interface breaks external fakes.
  Future<LaunchResult> launchSession(
    GameInstall install,
    LauncherProfile profile,
    LaunchSessionRequest session, {
    LaunchSelection? selectionOverride,
    bool restart = false,
  }) => _launchSession(
    install,
    profile,
    session,
    selectionOverride: selectionOverride,
    restart: restart,
  );

  /// The exact archives installed in [install] and offered by the player's
  /// package sources, history included, for planning a join
  /// ([JoinPlanner.planFromCatalog]).
  Future<ExactPackageCatalog> loadExactPackageCatalog(GameInstall install) =>
      _tracked(() => _loadExactPackageCatalog(install));

  @override
  Future<DiagnosticBundle> createDiagnosticBundle(
    GameInstall install,
    DependencyResolutionResult resolution,
  ) => _tracked(() => _createDiagnosticBundle(install, resolution));

  @override
  Future<String> readRecentLog(GameInstall install, {int maxLines = 200}) =>
      _tracked(() => _readRecentCombinedLog(install, maxLines: maxLines));

  @override
  Future<void> openPath(String path) => _tracked(() => _openPath(path));

  @override
  Future<void> openContainingFolder(String path) =>
      _tracked(() => _openPath(File(path).absolute.parent.path));

  @override
  Future<void> ensureDirectory(String path) =>
      _tracked(() => Directory(path).create(recursive: true));

  /// Closes the repository once nothing it started still runs ([_close]).
  @override
  Future<void> dispose() => _disposal ??= _close();
}
