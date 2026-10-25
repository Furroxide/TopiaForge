part of '../local_launcher_repository.dart';

/// [MultiplayerSessionHost] for [LocalLauncherRepository]: a join's plan from
/// what the install holds, who runs the game, and the join's start.
///
/// The start follows the ownership rules a restart follows. A game this
/// launcher started that already runs exactly the server profile's packages
/// takes the join live (`session-join`), with no restart, unless the target
/// is an address, which only a launch request may carry. One that runs other
/// packages is asked to quit, as its QUIT button would, and only when the
/// player's consent covered closing it. A game this launcher did not start is
/// the player's to close. A start that would open in safe mode, where a
/// session is refused, is not made at all.
mixin _JoinSessionHosting implements MultiplayerSessionHost {
  LocalLauncherRepository get _launcher => this as LocalLauncherRepository;

  /// The live joins staged for running games, by join id, so a cancel can
  /// withdraw one the game has not taken yet.
  final Map<String, SessionJoinRequest> _liveJoins = {};

  /// The install each game this launcher started runs from, by the install's
  /// launch identity.
  final Map<String, GameInstall> _ownedLaunchInstalls = {};

  /// Where handoffs go while anyone listens, and the look that finds them.
  StreamController<JoinHandoffArrival>? _handoffs;
  Timer? _handoffTimer;
  bool _handoffLooking = false;

  @override
  Future<JoinEvent?> planJoinContent(
    GameInstall install,
    JoinSessionState state, {
    bool developerMode = false,
  }) => _launcher._tracked(
    () => _launcher._planJoinContent(
      install,
      state,
      developerMode: developerMode,
    ),
  );

  @override
  Future<JoinGameRunning> joinGameRunning(GameInstall install) =>
      _launcher._tracked(() => _launcher._joinGameRunning(install));

  @override
  Future<JoinLaunch> launchJoinSession(
    GameInstall install,
    JoinSessionState state,
    LaunchSessionRequest session, {
    Future<JoinStartPreparation> Function()? beforeStart,
  }) => _launcher._tracked(() async {
    try {
      return await _launcher._startJoinSession(
        install,
        state,
        session,
        beforeStart,
      );
    } on Object catch (error) {
      await _launcher._appendLauncherLogBestEffort(
        'A join could not start the game: ${error.runtimeType}.',
      );
      return JoinLaunchRefused(_joinNotStarted);
    }
  });

  @override
  Future<bool> withdrawJoinLaunch(
    GameInstall install,
    JoinLaunchStarted launch,
  ) => _launcher._tracked(() async {
    final join = _liveJoins.remove(launch.requestId);
    if (!launch.live || join == null) return false;
    return LaunchStagingStore(install.path).withdrawSessionJoin(join);
  });

  @override
  Stream<JoinHandoffArrival> get sessionHandoffs => _launcher._handoffStream();
}

extension _JoinSessionLaunching on LocalLauncherRepository {
  /// [MultiplayerSessionHost.planJoinContent]: the exact catalog, read right
  /// before consent, and the environment of this install and the bundled
  /// runtime.
  Future<JoinEvent?> _planJoinContent(
    GameInstall install,
    JoinSessionState state, {
    required bool developerMode,
  }) async {
    final descriptor = state.descriptor;
    if (state.stage != JoinStage.checking || descriptor == null) return null;
    final build = install.gameVersion;
    if (build == null) {
      return JoinFailed(
        JoinFailureCode.gameBuildMismatch,
        detail:
            'The launcher could not tell which Robotopia build this install '
            'is, so it cannot check the server. Select the install again in '
            'Setup, then join again.',
      );
    }
    final ExactPackageCatalog catalog;
    try {
      catalog = await _loadExactPackageCatalog(install);
    } on Object catch (error) {
      await _appendLauncherLogBestEffort(
        'A join could not read what is installed: ${_failureText(error)}',
      );
      return JoinFailed(
        JoinFailureCode.contentUnavailable,
        detail:
            "The launcher could not read what is installed just now, so it "
            "cannot plan the join. Close any program that may be using the "
            "game's files, then join again.",
      );
    }
    final plan = const JoinPlanner().planFromCatalog(
      server: descriptor,
      catalog: catalog,
      environment: JoinEnvironment(
        gameBuild: build,
        runtimeVersion: _loaderVersion,
        sdkVersion: _sdkVersion,
        protocolVersion: TopiaForgeRuntimeVersions.multiplayerProtocolVersion,
        protocolPeerVersionRange:
            TopiaForgeRuntimeVersions.multiplayerPeerVersionRange,
        platform: _gamePlatform(install),
        architecture: _gameArchitecture(install),
        developerMode: developerMode,
      ),
      mayContactHost: state.mayContactHost,
    );
    return JoinPlanReady(plan);
  }

  /// [MultiplayerSessionHost.joinGameRunning]. A game counts as this
  /// launcher's only while its process verifies; one that cannot be told
  /// counts as another program's, which a join never closes.
  Future<JoinGameRunning> _joinGameRunning(GameInstall install) async {
    try {
      if (!await _runningForLaunch(install)) return JoinGameRunning.none;
      final process = _ownedLaunchProcesses[_launchInstallIdentity(install)];
      if (process == null) return JoinGameRunning.foreign;
      return await _gameProcessLiveness(process) == true
          ? JoinGameRunning.owned
          : JoinGameRunning.foreign;
    } on Object {
      return JoinGameRunning.foreign;
    }
  }

  Future<JoinLaunch> _startJoinSession(
    GameInstall install,
    JoinSessionState state,
    LaunchSessionRequest session,
    Future<JoinStartPreparation> Function()? beforeStart,
  ) async {
    final plan = state.plan;
    if (state.stage != JoinStage.launching ||
        plan == null ||
        session.role != LaunchSessionRole.join) {
      throw ArgumentError('Only a join that is launching starts.', 'state');
    }
    if (_disposed) return const JoinLaunchStopped();
    final profile = await _savedServerProfile(plan.derivedProfile.id);
    if (profile == null) {
      return JoinLaunchRefused(
        JoinFailed(
          JoinFailureCode.startFailed,
          detail:
              "The server's profile is missing, so nothing was started. Join "
              'again to save it.',
        ),
      );
    }
    final identity = _launchInstallIdentity(install);
    ReplacedGame? replaced;
    if (await _runningForLaunch(install)) {
      final process = _ownedLaunchProcesses[identity];
      if (process == null || await _gameProcessLiveness(process) != true) {
        return const JoinLaunchRefused(_foreignGameAtLaunch);
      }
      final live = await _stageLiveJoin(install, profile, process, session);
      if (live != null) return live;
      if (!state.closeOwnedGameAcknowledged) {
        return const JoinLaunchRefused(_ownedGameAtLaunch);
      }
      replaced = await _closeOwnedGame(install, process);
      if (replaced == null) return const JoinLaunchStopped();
      await _forgetOwnedGame(identity, process);
      if (await _runningForLaunch(install)) {
        return const JoinLaunchRefused(_foreignGameAtLaunch);
      }
      if (replaced.safeMode) {
        return JoinLaunchRefused(
          JoinFailed(
            JoinFailureCode.launchBlocked,
            detail: '${replaced.message(started: false)} $_safeModeNextStep',
          ),
        );
      }
    } else if (await _nextStartOpensInSafeMode(install)) {
      return JoinLaunchRefused(
        JoinFailed(
          JoinFailureCode.launchBlocked,
          detail:
              "Robotopia's last run did not close cleanly, so its next start "
              'opens in safe mode, with every mod off. $_safeModeNextStep',
        ),
      );
    }
    if (_disposed) return const JoinLaunchStopped();
    final prepared = await beforeStart?.call();
    if (prepared?.failure case final failure?) {
      return JoinLaunchRefused(failure);
    }
    final startSession = prepared == null ? session : prepared.session;
    if (startSession == null) return JoinLaunchRefused(_joinNotStarted);
    final result = await _launchSession(
      install,
      profile,
      startSession,
      selectionOverride: null,
      restart: false,
    );
    final requestId = result.requestId;
    if (!result.started || requestId == null) {
      if (_disposed) return const JoinLaunchStopped();
      final needsRepair = (await _validateGameDirectory(
        install.path,
      )).needsRepair;
      await _appendLauncherLogBestEffort(
        'A join did not start the game${needsRepair ? '; the runtime needs '
                  'a repair' : ''}.',
      );
      return JoinLaunchRefused(_launchRefusal(result, needsRepair));
    }
    await _appendLauncherLogBestEffort('A join started the game.');
    return JoinLaunchStarted(
      requestId: requestId,
      live: false,
      activity: result.latestActivity,
      replacedGame: replaced,
    );
  }

  /// A live join for the running game [process] when it runs exactly the
  /// packages [profile] pins and [session]'s target is not an address; null
  /// when the game must start again for the join.
  Future<JoinLaunchStarted?> _stageLiveJoin(
    GameInstall install,
    LauncherProfile profile,
    LaunchProcessIdentity process,
    LaunchSessionRequest session,
  ) async {
    if (session.target is LaunchDirectTarget) return null;
    final requestId = _ownedLaunchRequests[process];
    final running = requestId == null
        ? null
        : _launchMonitors[requestId]?.current;
    if (running == null) return null;
    final preview = await _previewLaunch(
      install,
      profile,
      selectionOverride: const LaunchSelection.mainMenu(),
    );
    if (!preview.canLaunch || preview.packageDigest != running.packageDigest) {
      return null;
    }
    final join = SessionJoinRequest(
      requestId: running.requestId,
      joinId: _newLaunchRequestId(),
      packageSetDigest: running.packageDigest,
      session: session,
    );
    final store = LaunchStagingStore(install.path);
    await store.writeSessionJoin(join);
    final activity = LaunchActivity(
      requestId: join.joinId,
      profileId: profile.id,
      profileRevision: profile.revision,
      installIdentity: running.installIdentity,
      packageDigest: running.packageDigest,
      command: session.command,
      process: process,
      unconfirmed: true,
    );
    final monitor = LaunchActivityMonitor(
      store: store,
      current: activity,
      processAlive: _gameProcessLiveness,
      onChanged: (value) {
        if (!_disposed) _launchActivities.add(value);
      },
    );
    _liveJoins[join.joinId] = join;
    _launchMonitors[join.joinId] = monitor;
    if (!_disposed) _launchActivities.add(activity);
    await monitor.start();
    await _appendLauncherLogBestEffort(
      'Staged a live join for the game this launcher started.',
    );
    return JoinLaunchStarted(
      requestId: join.joinId,
      live: true,
      activity: monitor.current,
    );
  }

  /// The saved server profile [id], or null when there is none.
  Future<LauncherProfile?> _savedServerProfile(String id) async {
    final (stored, _) = await _readProfileStore();
    for (final profile in stored) {
      if (profile.id == id && ServerProfiles.isServerProfile(profile)) {
        return profile;
      }
    }
    return null;
  }

  /// Lets go of [process], a game this launcher started that has ended.
  Future<void> _forgetOwnedGame(
    String identity,
    LaunchProcessIdentity process,
  ) async {
    _ownedLaunchProcesses.remove(identity);
    _ownedLaunchRequests.remove(process);
    _ownedLaunchInstalls.remove(identity);
    final retired = _launchMonitors.entries
        .where((entry) => entry.value.current.installIdentity == identity)
        .toList();
    for (final entry in retired) {
      await entry.value.dispose();
      _launchMonitors.remove(entry.key);
      _liveJoins.remove(entry.key);
    }
  }
}

/// What a launch that did not start means for the join, for the player. The
/// launch's own message is not repeated: it can hold what the launcher saw
/// underneath.
JoinFailed _launchRefusal(LaunchResult result, bool needsRepair) {
  if (needsRepair) {
    return JoinFailed(
      JoinFailureCode.repairNeeded,
      detail:
          'The TopiaForge runtime in this install is missing or out of date, '
          'so nothing was started. Repair it, then join again.',
    );
  }
  final reasons = [
    for (final issue in result.issues)
      if (issue.isBlocking) issue.message,
    for (final block in result.blocks) block.message,
  ];
  if (reasons.isEmpty) return _joinNotStarted;
  return JoinFailed(
    JoinFailureCode.launchBlocked,
    detail: "${reasons.join(' ')} Nothing was started.",
  );
}

const _safeModeNextStep =
    'A game in safe mode cannot join a session. Start Robotopia from the '
    'launcher once and quit it normally, then join again.';

final _joinNotStarted = JoinFailed(
  JoinFailureCode.startFailed,
  detail:
      'Robotopia could not be started for the join. Try again; if it keeps '
      'failing, open Diagnostics and look at the launcher log.',
);

const _ownedGameAtLaunch = JoinFailed.gameRunning(
  ownership: JoinGameOwnership.owned,
  detail:
      'Robotopia is running, and it runs other mods than the server. Save '
      'your game and quit it, then join again. Nothing was started.',
);

const _foreignGameAtLaunch = JoinFailed.gameRunning(
  ownership: JoinGameOwnership.foreign,
  detail:
      'Robotopia is running, and this launcher did not start it. Close the '
      'game, then join again. Nothing was started.',
);
