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

  Future<LaunchResult> _launchProfile(
    GameInstall install,
    LauncherProfile source, {
    LaunchSelection? selectionOverride,
    required bool restart,
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
    try {
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
      if (prepared.failure != null) return prepared.failure!;
      final current = prepared.install!;
      final preview = await _previewLaunch(
        current,
        profile,
        selectionOverride: selectionOverride,
      );
      if (!preview.canLaunch) return _previewLaunchFailure(preview);
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
        await _gameProcessStopper(process);
      }
      _ownedLaunchProcesses.remove(identity);
      final retired = _launchMonitors.entries
          .where((entry) => entry.value.current.installIdentity == identity)
          .toList();
      for (final entry in retired) {
        await entry.value.dispose();
        _launchMonitors.remove(entry.key);
      }
      return await _startGame(
        current,
        profile,
        selectionOverride: selectionOverride,
        message: profile.launchSettings.safeMode
            ? 'Started TopiaForge process in Safe Mode for this run only.'
            : restart
            ? 'Started replacement TopiaForge process.'
            : 'Started TopiaForge process.',
      );
    } on Object catch (error) {
      return LaunchResult(
        started: false,
        message: 'Launch could not proceed: $error',
      );
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
