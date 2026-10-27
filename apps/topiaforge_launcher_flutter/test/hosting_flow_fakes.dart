part of 'hosting_flow_test.dart';

final class _Rig
    implements
        LauncherRepository,
        MultiplayerHostingGateway,
        MultiplayerContentHost {
  final activities = StreamController<LaunchActivity>.broadcast(sync: true);
  final install = const GameInstall(
    path: 'fixture',
    executablePath: 'fixture/game.exe',
    bepInExStatus: ComponentState.ready,
    loaderStatus: ComponentState.ready,
  );
  final profile = LauncherProfile.defaultProfile();
  final other = const LauncherProfile(id: 'other', name: 'Other');
  var selected = 'default';
  var preferences = MultiplayerPreferences(
    settings: MultiplayerSettings(),
    hosting: MultiplayerHostingDefaults.forPlayer('Player'),
  );
  final choices = HostingChoices(
    defaults: MultiplayerHostingDefaults(name: 'Evening'),
  );
  Completer<HostingPreparation>? prepareGate;
  Completer<void>? startGate;
  Completer<void>? saveGate;
  Completer<ServerContentCleanupPlan>? cleanupGate;
  CancellationToken? token;
  int launches = 0;
  ServerContentCleanupPlan? applied;
  ServerContentCleanupRefusal? refusal = ServerContentCleanupRefusal.stale;
  var loads = 0;
  final catalog = ServerContentCleanupPlan(
    versions: [
      ServerContentVersion(
        modId: 'server.tools',
        version: '1.0.0',
        sha256: 'a' * 64,
        receiptSha256: 'b' * 64,
        sizeBytes: 4096,
        serverProfileIds: const ['server-one'],
      ),
    ],
    profiles: const [
      ServerContentProfile(
        id: 'server-one',
        name: 'Evening server',
        revision: 1,
      ),
    ],
  );
  HostingPlan plan() => HostingPlan(
    profile: profile,
    preferences: preferences,
    choices: choices,
    installIdentity: 'install',
    packageDigest: 'digest',
    joinability: 'Local network only.',
  );
  @override
  String get dataRoot => 'fixture/data';
  @override
  Stream<LaunchActivity> get launchActivities => activities.stream;
  @override
  Future<LauncherSnapshot> loadSnapshot() async {
    loads++;
    return LauncherSnapshot(
      profiles: [profile, other],
      selectedProfileId: selected,
      installedMods: const [],
      registryMods: const [],
      packageSources: const [],
      recentLog: '',
      gameInstall: install,
    );
  }

  @override
  Future<List<LauncherProfile>> saveProfiles(
    List<LauncherProfile> profiles,
    String selectedProfileId,
  ) async {
    await saveGate?.future;
    selected = selectedProfileId;
    return profiles;
  }

  @override
  Future<LaunchPreview> previewLaunch(
    GameInstall install,
    LauncherProfile profile, {
    LaunchSelection? selectionOverride,
  }) async => LaunchPreview(
    profileId: profile.id,
    profileRevision: profile.revision,
    installIdentity: 'install',
    packageDigest: 'digest',
    requestedSelection: profile.launchSelection,
    effectiveSelection: const LaunchSelection.mainMenu(),
  );
  @override
  Future<HostingPreparation> prepareHosting(
    GameInstall install,
    LauncherProfile profile,
    HostingChoices choices, {
    required bool developerMode,
    required CancellationToken cancellation,
  }) async {
    token = cancellation;
    return prepareGate == null ? plan() : prepareGate!.future;
  }

  @override
  Future<LaunchResult> startHosting(
    GameInstall install,
    HostingPlan plan, {
    required String intentId,
    required bool acknowledgeDisclosure,
    required bool developerMode,
    required CancellationToken cancellation,
  }) async {
    token = cancellation;
    await startGate?.future;
    if (cancellation.isCancelled) {
      return const LaunchResult(started: false, message: 'Cancelled');
    }
    launches++;
    return const LaunchResult(started: false, message: 'Fake start refused');
  }

  @override
  Future<ServerContentCleanupPlan> planServerContentCleanup(
    GameInstall install, {
    Set<String>? serverProfileIds,
  }) async => cleanupGate != null
      ? cleanupGate!.future
      : serverProfileIds == null
      ? catalog
      : ServerContentCleanupPlan(
          versions: catalog.versions,
          profiles: catalog.profiles.where(
            (profile) => serverProfileIds.contains(profile.id),
          ),
          scope: serverProfileIds,
        );
  @override
  Future<ServerContentCleanupResult> applyServerContentCleanup(
    GameInstall install,
    ServerContentCleanupPlan plan,
  ) async {
    applied = plan;
    return refusal == null
        ? const ServerContentCleanupResult.applied(
            removedVersions: ['server.tools@1.0.0'],
            removedProfiles: ['server-one'],
            freedBytes: 4096,
          )
        : ServerContentCleanupResult.refused(refusal!, '203.0.113.77 hidden');
  }

  @override
  Future<MultiplayerPreferences> loadPreferences() async => preferences;
  @override
  Future<MultiplayerPreferencesRound> syncWithGame(
    GameInstall install, {
    CancellationToken? cancellation,
  }) async => MultiplayerPreferencesSynced(preferences, changedInGame: false);
  @override
  Future<GameRunState> gameRunState(GameInstall install) async =>
      GameRunState.notRunning;
  @override
  MultiplayerServiceStatus get serviceStatus =>
      MultiplayerServiceStatus.unconfigured;
  @override
  LanBrowseSnapshot get lanSnapshot => LanBrowseSnapshot.stopped;
  @override
  Stream<LanBrowseSnapshot> get lanSnapshots => const Stream.empty();
  @override
  Future<void> dispose() async {
    if (!activities.isClosed) await activities.close();
  }

  @override
  dynamic noSuchMethod(Invocation invocation) => super.noSuchMethod(invocation);
}
