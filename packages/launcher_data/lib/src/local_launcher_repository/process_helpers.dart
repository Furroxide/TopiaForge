part of '../local_launcher_repository.dart';

extension _ProcessHelpers on LocalLauncherRepository {
  Future<LaunchResult> _startGame(
    GameInstall install,
    LauncherProfile profile, {
    LaunchSelection? selectionOverride,
    required String message,
  }) async {
    final acceptance = _acceptanceIsolation;
    if (acceptance != null) {
      acceptance.verify();
      if (_acceptanceChallenge == null ||
          !RegExp(r'^[0-9a-f]{64}$').hasMatch(_acceptanceChallenge)) {
        throw StateError(
          'Acceptance launch requires its private challenge before staging.',
        );
      }
      if (!sameAcceptancePath(install.path, acceptance.gameRoot) ||
          profile.launchSettings.extraArguments.isNotEmpty ||
          profile.launchSettings.environment.isNotEmpty) {
        throw StateError(
          'Acceptance forbids another install or profile process overrides.',
        );
      }
    }
    final refreshed = await _validateGameDirectory(install.path);
    if (refreshed.needsRepair) {
      return const LaunchResult(
        started: false,
        message:
            'TopiaForge runtime is missing or stale. Repair Runtime before launch.',
      );
    }
    final layout = GameLayout.resolve(refreshed.path);
    if (layout == null || !File(layout.executablePath).existsSync()) {
      return const LaunchResult(
        started: false,
        message: 'The Robotopia game was not found.',
      );
    }
    var executable = layout.executablePath;
    var arguments = profile.launchSettings.extraArguments;
    if (layout.kind == GameInstallLayout.linuxProton) {
      final settings = await _loadSettings();
      try {
        executable = configuredWineExecutable(settings['wineCommand']);
      } on FormatException catch (error) {
        return LaunchResult(started: false, message: error.message);
      }
      arguments = [layout.executablePath, ...arguments];
    }

    final preview = await _previewLaunch(
      refreshed,
      profile,
      selectionOverride: selectionOverride,
    );
    if (!preview.canLaunch) return _previewLaunchFailure(preview);
    final requestId = _newLaunchRequestId();
    final configuration = ProfileLaunchConfigurationV4(
      profileId: profile.id,
      profileRevision: profile.revision,
      requestId: requestId,
      command: preview.effectiveSelection.kind == LaunchSelectionKind.mainMenu
          ? 'main-menu'
          : 'launch-target',
      safeMode: profile.launchSettings.safeMode,
      inheritManagerModState: profile.inheritManagerModState,
      enabledMods: profile.enabledMods,
      selectedVersions: profile.selectedVersions,
      packages: preview.packages,
      digest: preview.packageDigest,
      plan: preview.resolution?.plan?.descriptor,
    );
    final store = LaunchStagingStore(refreshed.path);
    final launchFile = await store.writeRequest(configuration);
    late final Map<String, String> environment;
    try {
      if (acceptance == null) {
        environment = _profileLaunchEnvironment(
          layout,
          profile,
          launchFile.path,
        );
      } else {
        _acceptanceRequests[requestId] = await store.writeAcceptanceRequest(
          context: acceptance,
          profile: configuration,
          challenge: _acceptanceChallenge!,
          now: DateTime.now().toUtc(),
        );
        environment = acceptanceLaunchEnvironment(
          acceptance,
          launchFile.path,
          requestId,
        );
      }
      if (await _runningForLaunch(refreshed)) {
        await store.deleteRequest(configuration);
        return const LaunchResult(
          started: false,
          message:
              'TopiaForge is Busy: this install already has a running or unverified process.',
        );
      }
      final finalPreview = await _previewLaunch(
        refreshed,
        profile,
        selectionOverride: selectionOverride,
      );
      if (!finalPreview.canLaunch) {
        await store.deleteRequest(configuration);
        return _previewLaunchFailure(finalPreview);
      }
      if (finalPreview.packageDigest != preview.packageDigest ||
          _canonicalProfileValue(
                finalPreview.resolution?.plan?.descriptor.toJson(),
              ) !=
              _canonicalProfileValue(configuration.plan?.toJson()) ||
          finalPreview.effectiveSelection != preview.effectiveSelection) {
        await store.deleteRequest(configuration);
        return const LaunchResult(
          started: false,
          message:
              'Installed launch content changed during preflight. Refresh the selection and launch again.',
        );
      }
      await _requireCurrentProfileRevision(profile);
    } on Object {
      await store.deleteRequest(configuration);
      rethrow;
    }
    if (_disposed) {
      await store.deleteRequest(configuration);
      return const LaunchResult(
        started: false,
        message: 'The launcher repository closed before process creation.',
      );
    }
    final LaunchProcessReceipt receipt;
    try {
      receipt = await _createGameProcess(
        GameProcessRequest(
          executable: executable,
          arguments: arguments,
          workingDirectory: layout.gameRoot,
          environment: environment,
          inheritParentEnvironment: acceptance == null,
          requiredWindowsIdentity: acceptance?.identity,
        ),
        layout.executablePath,
      );
    } on Object {
      await store.deleteRequest(configuration);
      return LaunchResult(
        started: false,
        requestId: requestId,
        message: 'TopiaForge could not be started. No mod state was changed.',
      );
    }
    final processId = receipt.pid > 0 && receipt.pid <= 2147483647
        ? receipt.pid
        : null;
    final process = _correlatedReceiptIdentity(receipt, layout.executablePath);
    final identity = _launchInstallIdentity(refreshed);
    if (process != null && (!_disposed || acceptance != null)) {
      // Disposal closes monitoring, but cannot release a created acceptance
      // process: its original receipt must remain available for owned cleanup.
      _ownedLaunchProcesses[identity] = process;
      if (acceptance != null) _acceptanceReceipts[requestId] = process;
    }
    final activity = LaunchActivity(
      requestId: requestId,
      profileId: profile.id,
      profileRevision: profile.revision,
      installIdentity: identity,
      packageDigest: configuration.digest,
      command: configuration.command,
      process: process,
      unconfirmed: true,
    );
    if (_disposed) {
      return LaunchResult(
        started: true,
        message:
            '$message Runtime acknowledgement is unconfirmed because the launcher closed.',
        processId: processId,
        process: process,
        requestId: requestId,
        latestActivity: activity,
      );
    }
    final monitor = LaunchActivityMonitor(
      store: store,
      current: activity,
      processAlive: _gameProcessLiveness,
      onChanged: (value) {
        if (!_disposed) _launchActivities.add(value);
      },
    );
    _launchMonitors[requestId] = monitor;
    if (!_disposed) _launchActivities.add(activity);
    await monitor.start();
    await _appendLauncherLogBestEffort(
      'Started process for request $requestId pid=$processId; runtime session acknowledgement is separate.',
    );
    return LaunchResult(
      started: true,
      message:
          '$message Runtime acknowledgement is ${monitor.current.acknowledged ? 'available.' : 'unconfirmed.'}',
      processId: processId,
      process: process,
      requestId: requestId,
      latestActivity: monitor.current,
    );
  }
}

Future<LaunchProcessReceipt> _startGameWithReceipt(
  GameProcessRequest request,
) => startLaunchProcessWithReceipt(
  executable: request.executable,
  arguments: request.arguments,
  workingDirectory: request.workingDirectory,
  environment: request.environment,
  inheritParentEnvironment: request.inheritParentEnvironment,
  requiredWindowsIdentity: request.requiredWindowsIdentity,
);

extension _ProcessCreation on LocalLauncherRepository {
  Future<LaunchProcessReceipt> _createGameProcess(
    GameProcessRequest request,
    String expectedImage,
  ) async {
    final creator = _gameProcessCreator;
    if (creator != null) return creator(request);
    final processId = await _gameProcessStarter!(request);
    LaunchProcessIdentity? identity;
    try {
      identity = await _gameProcessIdentityReader(processId, expectedImage);
    } on Object {
      identity = null;
    }
    return LaunchProcessReceipt(pid: processId, identity: identity);
  }
}

LaunchProcessIdentity? _correlatedReceiptIdentity(
  LaunchProcessReceipt receipt,
  String expectedImage,
) {
  final identity = receipt.identity;
  if (identity == null ||
      receipt.pid <= 0 ||
      receipt.pid > 2147483647 ||
      identity.pid != receipt.pid ||
      !identity.startTimeUtc.isUtc ||
      identity.nativeStartToken.isEmpty ||
      !p.isAbsolute(identity.executablePath) ||
      identity.executablePath.contains('\u0000')) {
    return null;
  }
  try {
    var expected = File(expectedImage).resolveSymbolicLinksSync();
    var actual = File(identity.executablePath).resolveSymbolicLinksSync();
    if (Platform.isWindows) {
      expected = expected.toLowerCase();
      actual = actual.toLowerCase();
    }
    return actual == expected ? identity : null;
  } on FileSystemException {
    return null;
  }
}

/// Unknown liveness retains Busy and restart-required state.
Future<bool> _defaultGameRunningProbe(GameInstall install) async {
  final layout = GameLayout.resolve(install.path);
  if (layout == null) return true;
  return await probeLaunchProcessRunning(layout.executablePath) ?? true;
}
