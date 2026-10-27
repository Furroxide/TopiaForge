part of '../local_launcher_repository.dart';

String _newLaunchRequestId() {
  final random = Random.secure();
  return List.generate(
    16,
    (_) => random.nextInt(256).toRadixString(16).padLeft(2, '0'),
  ).join();
}

Future<LaunchProcessIdentity?> _unverifiedInjectedProcess(
  int pid,
  String executable,
) async => null;

extension _LaunchAdmission on LocalLauncherRepository {
  Future<bool> _runningForLaunch(GameInstall install) async {
    try {
      return await _gameRunningProbe(install);
    } on Object {
      return true;
    }
  }

  LaunchResult _previewLaunchFailure(LaunchPreview preview) => LaunchResult(
    started: false,
    issues: preview.issues,
    blocks: preview.blocks,
    message: [
      ...preview.issues
          .where((issue) => issue.isBlocking)
          .map((issue) => issue.message),
      ...preview.blocks.map((block) => block.message),
      if (preview.effectiveSelection.kind ==
          LaunchSelectionKind.unresolvedLegacy)
        'The remembered launch selection is unavailable or ambiguous. Select an explicit target or Main Menu to repair it.',
    ].join(' '),
  );

  /// [LocalLauncherRepository.launchSession]: refuses before anything
  /// happens what launch request V5 cannot carry, and starts a join at the
  /// main menu.
  Future<LaunchResult> _launchSession(
    GameInstall install,
    LauncherProfile profile,
    LaunchSessionRequest session, {
    required LaunchSelection? selectionOverride,
    required bool restart,
    CancellationToken? cancellation,
    String? expectedPackageDigest,
  }) {
    if (session.role == LaunchSessionRole.join &&
        selectionOverride != null &&
        selectionOverride.kind != LaunchSelectionKind.mainMenu) {
      throw ArgumentError(
        'A join starts at the main menu.',
        'selectionOverride',
      );
    }
    final refusal = _acceptanceIsolation != null
        ? 'Acceptance runs never host or join a session.'
        : profile.launchSettings.safeMode
        ? 'Safe Mode never hosts or joins a session. Turn Safe Mode off for '
              'this profile, then try again.'
        : session.role == LaunchSessionRole.join &&
              profile.inheritManagerModState
        ? "A join runs exactly the packages it planned, so its profile cannot "
              "inherit the manager's mod state."
        : null;
    if (refusal != null) {
      return Future.value(
        LaunchResult(
          started: false,
          message: '$refusal No process was stopped.',
        ),
      );
    }
    return _launchProfile(
      install,
      profile,
      selectionOverride: session.role == LaunchSessionRole.join
          ? const LaunchSelection.mainMenu()
          : selectionOverride,
      restart: restart,
      session: session,
      cancellation: cancellation,
      expectedPackageDigest: expectedPackageDigest,
    );
  }

  Future<LaunchResult> _launchProfile(
    GameInstall install,
    LauncherProfile source, {
    LaunchSelection? selectionOverride,
    required bool restart,
    LaunchSessionRequest? session,
    CancellationToken? cancellation,
    String? expectedPackageDigest,
  }) => _tracked(
    () => _admitLaunch(
      install,
      source,
      selectionOverride: selectionOverride,
      restart: restart,
      session: session,
      cancellation: cancellation,
      expectedPackageDigest: expectedPackageDigest,
    ),
  );

  /// Admits one launch at a time per install. A launch admitted before the
  /// repository closes stops at its next step with "The launcher repository
  /// is closed.", which the close waits for ([_close]).
  Future<LaunchResult> _admitLaunch(
    GameInstall install,
    LauncherProfile source, {
    LaunchSelection? selectionOverride,
    required bool restart,
    LaunchSessionRequest? session,
    CancellationToken? cancellation,
    String? expectedPackageDigest,
  }) async {
    if (_disposed) {
      return const LaunchResult(
        started: false,
        message: 'The launcher repository is closed.',
      );
    }
    final identity = _launchInstallIdentity(install);
    if (!_launchAdmissions.add(identity)) {
      return const LaunchResult(
        started: false,
        message:
            'TopiaForge is Busy preparing another launch for this install.',
      );
    }
    LaunchStagingLease? lease;
    ReplacedGame? replaced;
    try {
      cancellation?.throwIfCancelled();
      final profile = _requireValidLauncherProfile(
        LauncherProfile.fromJson(
          jsonDecode(jsonEncode(source.toJson())) as Map<String, Object?>,
        ),
      );
      _requireRuntimeDirectory(
        Directory(install.path),
        _managerRoot(install),
        label: 'Launch manager root',
      );
      _requireRuntimeDirectory(
        Directory(install.path),
        _managerStaging(install),
        label: 'Launch staging',
      );
      lease = await LaunchStagingStore(install.path).acquireLaunchLease();
      if (lease == null) {
        return const LaunchResult(
          started: false,
          message:
              'TopiaForge is Busy preparing a launch in another launcher or CLI instance.',
        );
      }
      if (_disposed) {
        return const LaunchResult(
          started: false,
          message: 'The launcher repository is closed.',
        );
      }
      final prepared = await _prepareRuntimeForLaunch(install);
      cancellation?.throwIfCancelled();
      if (prepared.failure != null) return prepared.failure!;
      final current = prepared.install!;
      final preview = await _previewLaunch(
        current,
        profile,
        selectionOverride: selectionOverride,
      );
      if (!preview.canLaunch) return _previewLaunchFailure(preview);
      if (expectedPackageDigest != null &&
          preview.packageDigest != expectedPackageDigest) {
        return const LaunchResult(
          started: false,
          message: 'The reviewed host packages changed. Review hosting again.',
        );
      }
      await _requireCurrentProfileRevision(profile);
      final running = await _runningForLaunch(current);
      if (_disposed) {
        return const LaunchResult(
          started: false,
          message: 'The launcher repository is closed.',
        );
      }
      if (running) {
        if (!restart) {
          return const LaunchResult(
            started: false,
            message:
                'TopiaForge is Busy: this install has a running or unverified process.',
          );
        }
        final process = _ownedLaunchProcesses[identity];
        if (process == null) {
          return const LaunchResult(
            started: false,
            message:
                'The running game is not owned by this launcher request. Close it before restarting; no process was stopped.',
          );
        }
        final alive = await _gameProcessLiveness(process);
        if (alive != true) {
          return const LaunchResult(
            started: false,
            message:
                'The previous process identity could not be verified. No process was stopped.',
          );
        }
        if (_disposed) {
          return const LaunchResult(
            started: false,
            message: 'The launcher repository is closed.',
          );
        }
        // Asked to quit, as its own Quit button would, and stopped only if
        // it does not, so the replacement is not forced into safe mode.
        replaced = await _closeOwnedGame(current, process);
        if (replaced == null) {
          return const LaunchResult(
            started: false,
            message:
                'The launcher repository closed while the running game was '
                'asked to quit. No process was stopped.',
          );
        }
      }
      // The game may have exited on its own since it was started, so its
      // receipt and the request it was started with go together.
      final dropped = _ownedLaunchProcesses.remove(identity);
      if (dropped != null) _ownedLaunchRequests.remove(dropped);
      final retired = _launchMonitors.entries
          .where((entry) => entry.value.current.installIdentity == identity)
          .toList();
      for (final entry in retired) {
        await entry.value.dispose();
        _launchMonitors.remove(entry.key);
      }
      await _stagePublishedMapSources(current);
      final started = await _startGame(
        current,
        profile,
        selectionOverride: selectionOverride,
        session: session,
        cancellation: cancellation,
        expectedPackageDigest: expectedPackageDigest,
        message: profile.launchSettings.safeMode
            ? 'Started TopiaForge process in Safe Mode for this run only.'
            : restart
            ? 'Started replacement TopiaForge process.'
            : 'Started TopiaForge process.',
      );
      return started.replacing(replaced);
    } on Object catch (error) {
      return LaunchResult(
        started: false,
        message: 'Launch could not proceed: $error',
      ).replacing(replaced);
    } finally {
      try {
        await lease?.release();
      } on Object catch (error) {
        await _appendLauncherLogBestEffort(
          'Launch admission release failed: $error',
        );
      }
      _launchAdmissions.remove(identity);
    }
  }
}
