part of 'join_flow_test.dart';

/// An address planted in every text the fakes control, so a test sees at
/// once if any of it travels further than it may.
const _planted = '203.0.113.77';
const _plantedEndpoint = '[2001:db8::77]:27250';

/// Host A's public keys, from `tests/fixtures/multiplayer/descriptor/`.
const _signingKey = 'X_63eOUQF6uSrrIZ1DhX5w-ouzCDzlMBavIFLxvmABE';
const _transportKey = 'RIcUrvBv2CmtHVUuyjEvJ7SN1V5vghS4UWaimHb8Y2s';

final class _AcceptAll implements ServerDescriptorSignatureVerifier {
  const _AcceptAll();

  @override
  Future<bool> verifySignature(SignedServerDescriptorV1 envelope) async => true;
}

ModManifest _manifest(
  String id,
  String version, {
  String? name,
  List<String> capabilities = const [],
}) => ModManifest.fromJson({
  'schemaVersion': 6,
  'name': id,
  'displayName': name ?? id,
  'version': version,
  'author': {'name': 'TopiaForge Tests'},
  'entryAssembly': 'Mod.dll',
  'entryType': '$id.Entry',
  'supportedGameVersionRange': '*',
  'supportedLoaderVersionRange': '*',
  'supportedSdkVersionRange': '*',
  'capabilities': capabilities,
});

String _sha(String seed) => PureSha256.hex(utf8.encode(seed));

/// A verified descriptor of host A's server, running [packages].
Future<VerifiedServerDescriptor> _server({
  String name = "Sam's Sandbox",
  List<DescriptorPackage>? packages,
  bool voice = true,
  bool passwordRequired = false,
  bool hostServesPackages = false,
  String gamemode = 'Open Sandbox',
  String world = 'Test City',
}) async {
  final descriptor = ServerDescriptorV1(
    signingKey: WireText.tryParseBase64Url(_signingKey, 32)!,
    transportKey: WireText.tryParseBase64Url(_transportKey, 32)!,
    issuedAt: 1791849600,
    name: name,
    gameBuild: '2545',
    runtimeVersion: '0.1.0-rc.1',
    protocolVersion: '1.0.0',
    maxPlayers: 8,
    passwordRequired: passwordRequired,
    voice: voice,
    visibility: ServerVisibility.public,
    hostServesPackages: hostServesPackages,
    gamemode: gamemode,
    world: world,
    packages: [...(packages ?? _packages)]
      ..sort((a, b) => a.id.compareTo(b.id)),
  );
  final envelope = SignedServerDescriptorV1.assemble(
    descriptor,
    Uint8List(SignedServerDescriptorV1.signatureBytes),
  );
  final result = await const ServerDescriptorVerifier(
    _AcceptAll(),
  ).verify(envelope.bytes);
  return (result as ServerDescriptorAccepted).descriptor;
}

final _packages = [
  DescriptorPackage(
    id: 'tf.zombies',
    version: '2.1.0',
    sha256: _sha('zombies'),
    role: DescriptorPackageRole.implicit,
  ),
  DescriptorPackage(
    id: 'tf.sandbox',
    version: '1.4.0',
    sha256: _sha('sandbox'),
    role: DescriptorPackageRole.implicit,
  ),
];

final _environment = JoinEnvironment(
  gameBuild: '0.0.2545',
  runtimeVersion: '0.1.0-rc.1',
  sdkVersion: '0.1.0-rc.1',
  protocolVersion: '1.0.0',
  platform: 'windows',
  architecture: 'x64',
);

/// Zombies to download from a source, Sandbox already installed.
JoinPlan _plan(VerifiedServerDescriptor server, {bool everything = false}) =>
    const JoinPlanner().plan(
      server: server,
      resolutions: {
        _sha('zombies'): everything
            ? JoinPackageResolution.installed(
                _manifest('tf.zombies', '2.1.0', name: 'Zombies'),
              )
            : JoinPackageResolution.available(
                manifest: _manifest(
                  'tf.zombies',
                  '2.1.0',
                  name: 'Zombies',
                  capabilities: ['hud', 'network'],
                ),
                sourceId: 'community',
                sourceName: 'Community Mods',
                downloadUrl: 'https://packages.example/zombies.topiaforgemod',
                sizeBytes: 3 * 1024 * 1024,
              ),
        _sha('sandbox'): JoinPackageResolution.installed(
          _manifest('tf.sandbox', '1.4.0', name: 'Sandbox'),
        ),
      },
      environment: _environment,
      mayContactHost: true,
    );

final _install = GameInstall(
  path: '/games/Robotopia',
  executablePath: '/games/Robotopia/Robotopia.exe',
  bepInExStatus: ComponentState.ready,
  loaderStatus: ComponentState.ready,
  gameVersion: '0.0.2545',
  gameVersionLabel: 'build 2545',
);

/// A launcher repository with one install and the player's own profile.
/// What a join's activity reports is added with [report].
final class _JoinRepository implements LauncherRepository {
  _JoinRepository({this.install, List<LauncherProfile>? profiles})
    : profiles = profiles ?? [LauncherProfile.defaultProfile()];

  final GameInstall? install;
  List<LauncherProfile> profiles;
  final _activities = StreamController<LaunchActivity>.broadcast(sync: true);
  var disposed = false;

  void report(LaunchActivity activity) => _activities.add(activity);

  @override
  String get dataRoot => '/data/topiaforge';

  @override
  Stream<LaunchActivity> get launchActivities => _activities.stream;

  @override
  Future<LauncherSnapshot> loadSnapshot() async => LauncherSnapshot(
    profiles: profiles,
    selectedProfileId: 'default',
    installedMods: const [],
    registryMods: const [],
    packageSources: const [],
    recentLog: '',
    gameInstall: install,
  );

  @override
  Future<LaunchPreview> previewLaunch(
    GameInstall install,
    LauncherProfile profile, {
    LaunchSelection? selectionOverride,
  }) async => LaunchPreview(
    profileId: profile.id,
    profileRevision: profile.revision,
    installIdentity: install.executablePath,
    packageDigest: packageSetDigest(const []),
    requestedSelection: const LaunchSelection.mainMenu(),
    effectiveSelection: const LaunchSelection.mainMenu(),
  );

  @override
  Future<void> dispose() async {
    disposed = true;
    await _activities.close();
  }

  @override
  Object? noSuchMethod(Invocation invocation) =>
      throw UnimplementedError('${invocation.memberName}');
}

/// A scripted join gateway that records each call. Each stage answers as
/// the happy path does unless the test scripts it, and a held stage waits
/// until the test completes it or the join's token is cancelled.
final class _JoinGateway implements MultiplayerJoinGateway {
  _JoinGateway({MultiplayerPreferences? preferences, this.server, this.plan})
    : preferences =
          preferences ??
          MultiplayerPreferences(
            settings: MultiplayerSettings(
              displayName: 'Sam',
            ).acknowledgePrivacyDisclosure(),
          );

  MultiplayerPreferences preferences;
  VerifiedServerDescriptor? server;
  JoinPlan? plan;
  final calls = <String>[];
  final tokens = <JoinStage, CancellationToken?>{};
  final intents = <String>[];

  /// What a stage answers instead of the happy path.
  final answers = <JoinStage, JoinEvent? Function(JoinSessionState state)>{};

  /// Stages whose answer waits for the test.
  final held = <JoinStage, Completer<JoinEvent?>>{};

  /// What a held stage reports before it waits.
  final heldProgress = <JoinStage, JoinEvent>{};

  /// Every call throws this.
  Object? failure;
  JoinGameRunning running = JoinGameRunning.none;
  JoinLaunch Function(JoinSessionState state)? launch;
  Completer<JoinLaunch>? launchGate;
  final withdrawn = <JoinLaunchStarted>[];
  var withdraws = true;
  bool unreadable = false;
  bool failDisclosure = false;
  final recorded = <String>[];
  final _handoffs = StreamController<JoinHandoffArrival>.broadcast();

  void handOff(JoinHandoffArrival arrival) => _handoffs.add(arrival);

  /// Holds [stage] until [release] or a cancel.
  Completer<JoinEvent?> hold(JoinStage stage) => held[stage] = Completer();

  @override
  Future<JoinEvent?> answerJoin(
    JoinSessionState state, {
    CancellationToken? cancellation,
    GameInstall? install,
    void Function(JoinEvent progress)? onProgress,
    bool developerMode = false,
  }) async {
    calls.add('answer ${state.stage.name}');
    tokens[state.stage] = cancellation;
    final error = failure;
    if (error != null) throw error;
    if (install == null) throw ArgumentError('No install.');
    final gate = held[state.stage];
    if (gate != null) {
      if (heldProgress[state.stage] case final progress?) {
        onProgress?.call(progress);
      }
      // Work that can stop stops at once; an install that stops before it
      // changes anything undoes what it staged. Saving the profile does not
      // stop.
      if (state.stage != JoinStage.preparingProfile) {
        cancellation?.register(() {
          if (gate.isCompleted) return;
          gate.complete(
            state.stage == JoinStage.installing
                ? const JoinInstallUndone()
                : null,
          );
        });
      }
      return gate.future;
    }
    final scripted = answers[state.stage];
    if (scripted != null) return scripted(state);
    final server = this.server ??= await _server();
    return switch (state.stage) {
      JoinStage.resolving => JoinTargetResolved(server.serverId),
      JoinStage.fetchingDescriptor => JoinDescriptorVerified(server),
      JoinStage.checking => JoinPlanReady(plan ??= _plan(server)),
      JoinStage.downloading => () {
        onProgress?.call(
          JoinDownloadProgressed(bytesDone: 1024, bytesTotal: 4096),
        );
        return const JoinDownloadsCompleted();
      }(),
      JoinStage.installing => () {
        onProgress?.call(
          JoinInstallProgressed(packagesDone: 1, packagesTotal: 1),
        );
        return const JoinInstallCompleted();
      }(),
      JoinStage.preparingProfile => const JoinProfilePrepared(),
      _ => null,
    };
  }

  @override
  Future<JoinGameRunning> joinGameRunning(GameInstall install) async {
    calls.add('game');
    return running;
  }

  @override
  Future<JoinLaunch> launchJoin(
    JoinSessionState state, {
    required GameInstall install,
    required String intentId,
    bool developerMode = false,
  }) async {
    calls.add('launch');
    intents.add(intentId);
    final gate = launchGate;
    if (gate != null) return gate.future;
    return launch?.call(state) ?? _started();
  }

  @override
  Future<bool> withdrawJoinLaunch(
    GameInstall install,
    JoinLaunchStarted launch,
  ) async {
    calls.add('withdraw');
    withdrawn.add(launch);
    return withdraws;
  }

  @override
  Stream<JoinHandoffArrival> get handoffs => _handoffs.stream;

  @override
  Future<MultiplayerSettings> recordJoin({
    required String serverId,
    required String name,
  }) async {
    recorded.add('$serverId $name');
    return preferences.settings;
  }

  @override
  Future<MultiplayerPreferences> loadPreferences() async {
    calls.add('load');
    final error = failure;
    if (error != null) throw error;
    return unreadable
        ? MultiplayerPreferences(
            settings: MultiplayerSettings(),
            health: MultiplayerPreferencesHealth.unreadable,
          )
        : preferences;
  }

  @override
  Future<MultiplayerPreferences> changePreferences(
    MultiplayerPreferencesChange change,
  ) async {
    calls.add(change.acknowledgeDisclosure ? 'acknowledge' : 'change');
    if (failDisclosure) {
      throw const MultiplayerPreferencesException(
        'disk full at $_planted while saving',
      );
    }
    return preferences = change.applyTo(preferences);
  }

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
  Future<LanBrowseSnapshot> startLanBrowsing() async =>
      LanBrowseSnapshot.stopped;

  @override
  Future<LanBrowseSnapshot> stopLanBrowsing() async =>
      LanBrowseSnapshot.stopped;

  @override
  Future<MultiplayerNetworkReport> checkNetwork({
    bool checkPorts = false,
    int? gamePort,
    bool developerMode = false,
    CancellationToken? cancellation,
  }) => throw UnimplementedError();

  @override
  Future<void> dispose() async {
    calls.add('dispose');
    await _handoffs.close();
  }
}

/// The launch request id the fake game reports a started join under.
const _requestId = '0f1e2d3c4b5a69788796a5b4c3d2e1f0';

JoinLaunchStarted _started({
  bool live = false,
  String requestId = _requestId,
}) => JoinLaunchStarted(
  requestId: requestId,
  live: live,
  activity: _activity(requestId: requestId),
);

/// The join's activity as the game reports it: [state] for its session,
/// and a launch outcome when [succeeded] or a [failure] is set.
LaunchActivity _activity({
  String requestId = _requestId,
  LaunchSessionState? state,
  String? phase,
  bool succeeded = false,
  LaunchMultiplayerFailure? failure,
  String? detail,
  LaunchSessionEndReason? ended,
  bool exited = false,
  bool unconfirmed = false,
  LaunchOperationError? error,
}) {
  final progress = state == null
      ? null
      : LaunchProgress(
          requestId: requestId,
          sequence: 1,
          phase: phase ?? 'idle',
          session: LaunchProgressSession(
            role: LaunchSessionRole.join,
            intentId: 'join-test',
            state: state,
          ),
        );
  final settled = succeeded || failure != null || error != null;
  return LaunchActivity(
    requestId: requestId,
    profileId: 'mp-0123456789abcdef',
    profileRevision: 0,
    installIdentity: '/games/Robotopia/Robotopia.exe',
    packageDigest: packageSetDigest(const []),
    command: 'join-session',
    progress: progress,
    unconfirmed: unconfirmed,
    processExited: exited,
    launchOutcome: settled
        ? LaunchOutcome(
            kind: 'launch',
            requestId: requestId,
            command: 'join-session',
            sequence: 2,
            status: succeeded ? 'succeeded' : 'failed',
            phase: succeeded ? 'idle' : 'connecting',
            error: error,
            multiplayer: LaunchOutcomeMultiplayer(
              role: LaunchSessionRole.join,
              intentId: 'join-test',
              failure: failure,
              detail: detail,
            ),
          )
        : null,
    sessionOutcome: ended == null
        ? null
        : LaunchOutcome(
            kind: 'session',
            requestId: requestId,
            sequence: 3,
            status: 'succeeded',
            phase: 'idle',
            multiplayer: LaunchOutcomeMultiplayer(
              role: LaunchSessionRole.join,
              intentId: 'join-test',
              endReason: ended,
            ),
          ),
  );
}
