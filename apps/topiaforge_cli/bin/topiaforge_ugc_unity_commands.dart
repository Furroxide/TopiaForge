part of 'topiaforge.dart';

extension _TopiaForgeUgcCommands on _TopiaForgeCli {
  // Drives the UGC Automerge sidecar. The local-folder channel needs none of
  // this; this is for full web-editor parity and remote collaboration.
  Future<int> _ugc(List<String> args) async {
    final sub = args.firstOrNull;
    if (sub == null || sub == 'help' || sub == '--help') {
      stdout.writeln('Usage:');
      stdout.writeln(
        '  topiaforge ugc publish --file <project.json> [--sync url] [--doc url] [--scene id] [--session-file path]',
      );
      stdout.writeln(
        '  topiaforge ugc watch <folder> [--sync url] [--doc url] [--scene id] [--session-file path]',
      );
      stdout.writeln('  topiaforge ugc check [--watch folder] [--sync url]');
      stdout.writeln('  topiaforge ugc status [--watch folder]');
      stdout.writeln(
        '  topiaforge ugc setup [--transport localFolder|automerge] [--watch folder] [--sync url] [--doc url]',
      );
      stdout.writeln(
        '      [--scene id] [--auto-connect|--no-auto-connect] [--debounce ms] [--max-snapshot bytes]',
      );
      stdout.writeln('      [--project path] [--no-deploy]');
      stdout.writeln(
        '  topiaforge ugc cleanup [--project path]  Stop live sync and clear transient connection state.',
      );
      stdout.writeln(
        '  topiaforge ugc dev [--project path|name] [--new name [--dir path]] [--watch folder]',
      );
      stdout.writeln(
        '      [--scene id] [--scene-name n] [--environment env] [--transport localFolder|automerge] [--doc url]',
      );
      stdout.writeln('      [--update-companion] [--launch-game] [--dry-run]');
      stdout.writeln('  topiaforge ugc go-live');
      return 0;
    }

    if (sub == 'status') {
      return _ugcStatus(args.skip(1).toList());
    }
    if (sub == 'setup') {
      return _ugcSetup(args.skip(1).toList());
    }
    if (sub == 'dev') {
      return _ugcDev(args.skip(1).toList());
    }
    if (sub == 'go-live') {
      return _ugcGoLive(args.skip(1).toList());
    }
    if (sub == 'cleanup' || sub == 'stop') {
      return _ugcCleanup(args.skip(1).toList());
    }

    final sidecar = _findSidecar();
    if (sidecar == null) {
      stderr.writeln(
        'UGC Automerge sidecar not found (tools/ugc-automerge-sidecar/index.mjs). Run from the repo.',
      );
      return 1;
    }
    final sidecarDir = sidecar.directory;

    final forward = <String>[];
    switch (sub) {
      case 'publish':
      case 'check':
        if (sub == 'check') forward.add('--check');
        forward.addAll(args.skip(1));
        break;
      case 'watch':
        final folder = args.length > 1 ? args[1] : null;
        if (folder == null) {
          stderr.writeln('Usage: topiaforge ugc watch <folder> [...]');
          return 2;
        }
        forward.addAll(['--watch', folder, ...args.skip(2)]);
        break;
      default:
        stderr.writeln('Unknown ugc subcommand: $sub');
        return 1;
    }

    try {
      await sidecar.prepare(requireDependencies: sub != 'check');

      final process = await Process.start(
        'node',
        [sidecar.scriptPath, ...forward],
        workingDirectory: sidecarDir,
        mode: ProcessStartMode.inheritStdio,
      );
      return process.exitCode;
    } on ProcessException catch (error) {
      stderr.writeln(
        'Could not run Node.js (${error.message}). '
        'Install ${UgcNodeVersionPolicy.requirement} and retry.',
      );
      return 1;
    }
  }

  // Reads the game's UGC live-sync status handshake and lists watch-folder scenes.
  Future<int> _ugcStatus(List<String> args) async {
    final launcher = LocalLauncherRepository();
    final install =
        await launcher.detectKnownInstall() ??
        (await launcher.loadSnapshot()).gameInstall;
    if (install == null) {
      stderr.writeln(
        'No Robotopia install detected. Select one in the launcher first.',
      );
      return 1;
    }

    final status = await launcher.readUgcLiveSyncStatus(install);
    if (status == null) {
      stdout.writeln(
        'No UGC live-sync status yet. Launch the game once with the UgcLiveSync mod installed.',
      );
    } else {
      stdout.writeln(
        'status        : ${status.status}${status.isLive ? ' (live)' : ''}',
      );
      stdout.writeln('transport     : ${status.transport}');
      stdout.writeln(
        'default folder: ${status.defaultWatchFolder.isEmpty ? '(unknown)' : status.defaultWatchFolder}',
      );
      if (status.connectedDocumentUrl.isNotEmpty) {
        stdout.writeln('document      : ${status.connectedDocumentUrl}');
      }
      if (status.sceneId.isNotEmpty) {
        stdout.writeln('scene         : ${status.sceneId}');
      }
      if (status.lastAppliedUtc.isNotEmpty) {
        stdout.writeln('last applied  : ${status.lastAppliedUtc}');
      }
    }

    final folder = _option(args, '--watch') ?? status?.defaultWatchFolder ?? '';
    if (folder.isNotEmpty) {
      final inspection = await launcher.inspectWatchFolderScenes(folder);
      if (inspection.hasBlockingIssues) {
        throw FormatException(
          inspection.issues.firstWhere((issue) => issue.isBlocking).message,
        );
      }
      final scenes = inspection.scenes;
      stdout.writeln(
        'scenes        : ${scenes.isEmpty ? '(none found in $folder)' : scenes.map((s) => s.id).join(', ')}',
      );
    }
    return 0;
  }

  // Deploys the project's UGC live-sync config with auto-connect enabled and launches the game.
  Future<int> _ugcGoLive(List<String> args) async {
    final workspace = await developerRepository.loadDeveloperWorkspace();
    final base =
        workspace.project?.unityCompanion.liveSync ??
        const UgcLiveSyncSettings();
    var settings = _withAutoConnect(base);
    if (settings.transport == 'automerge' && settings.documentUrl.isEmpty) {
      final session = await _readPublisherSession();
      final connected = session == null
          ? null
          : UgcLiveSyncTransitions.connectPublisherSession(settings, session);
      if (connected == null) {
        stderr.writeln(
          'No active Automerge publisher session was found. Run '
          '`topiaforge ugc watch <folder> --session-file '
          '${p.join(developerRepository.developerDataRoot, 'ugc-session.json')}` '
          'or `topiaforge ugc dev`, then retry.',
        );
        return 1;
      }
      settings = connected;
    }
    return _launchGameWithLiveSync(settings);
  }

  Future<Map<String, Object?>?> _readPublisherSession() async {
    final file = File(
      p.join(developerRepository.developerDataRoot, 'ugc-session.json'),
    );
    if (!await file.exists()) {
      return null;
    }
    try {
      return readBoundedJsonObjectSync(file, maxBytes: CliFileLimits.session);
    } on Object {
      return null;
    }
  }

  // Stops the live game session (if running), clears captured Automerge state, and leaves durable authoring
  // preferences such as watch folder and scene intact.
  Future<int> _ugcCleanup(List<String> args) async {
    final projectOptionIndex = args.indexOf('--project');
    if (projectOptionIndex >= 0 &&
        (projectOptionIndex + 1 >= args.length ||
            args[projectOptionIndex + 1].startsWith('--'))) {
      throw UsageError('Usage: topiaforge ugc cleanup [--project path]');
    }
    final projectPath = _option(args, '--project');

    DeveloperWorkspace? workspace;
    var settings = const UgcLiveSyncSettings();
    if (projectPath != null) {
      workspace = await developerRepository.loadDeveloperWorkspace(
        projectPath: projectPath,
      );
      final project = workspace.project;
      if (project == null) {
        throw StateError('TopiaForge project was not found at $projectPath.');
      }
      settings = project.unityCompanion.liveSync;
    } else {
      try {
        workspace = await developerRepository.loadDeveloperWorkspace();
        settings = workspace.project?.unityCompanion.liveSync ?? settings;
      } on Object {
        // The repository preserves the deployed runtime config when there is
        // no discoverable developer project, using these defaults only if no
        // valid deployed config exists either.
      }
    }

    final launcher = LocalLauncherRepository(
      knownGamePath: Platform.environment['ROBOTOPIA_GAME_DIR'],
    );
    final failures = <String>[];
    String errorMessage(Object error) =>
        error is StateError ? error.message : error.toString();

    // Revoke the session lease first. Detached CLI publishers are not owned by
    // this repository instance, so deleting the lease is their stop signal.
    try {
      await launcher.stopUgcPublisher(waitForExit: true);
      stdout.writeln('Stopped the repository-owned Automerge publisher.');
    } on Object catch (error) {
      failures.add('owned publisher cleanup: ${errorMessage(error)}');
    }
    try {
      await launcher.revokeUgcPublisherSession();
      stdout.writeln('Revoked the shared Automerge publisher session.');
    } on Object catch (error) {
      failures.add('publisher session cleanup: ${errorMessage(error)}');
    }

    final project = workspace?.project;
    if (project != null) {
      try {
        await developerRepository.updateUgcLiveSync(
          workspace!.projectRoot,
          _withoutTransientUgcState(settings),
        );
        stdout.writeln(
          'Cleared transient live-sync state from ${workspace.projectRoot}/topiaforge.project.json.',
        );
      } on Object catch (error) {
        failures.add('project cleanup: ${errorMessage(error)}');
      }
    }

    GameInstall? install;
    try {
      install = await launcher.detectKnownInstall();
    } on Object catch (error) {
      failures.add('game install detection: ${errorMessage(error)}');
    }
    if (install == null) {
      try {
        install = (await launcher.loadSnapshot()).gameInstall;
      } on Object catch (error) {
        failures.add('saved game install lookup: ${errorMessage(error)}');
      }
    }
    if (install?.issues.any((issue) => issue.isBlocking) ?? false) {
      install = null;
    }

    if (install == null) {
      stdout.writeln(
        'No Robotopia install detected — skipped game-side live-sync cleanup.',
      );
    } else {
      try {
        final report = await launcher.cleanupUgcLiveSync(install, settings);
        stdout.writeln(
          'Requested UGC live-sync stop via ${report.commandPath}.',
        );
        stdout.writeln('Deployed cleanup config to ${report.configPath}.');
        if (report.statusFileDeleted) {
          stdout.writeln('Removed stale live-sync status.');
        }
        if (report.sessionFileDeleted) {
          stdout.writeln('Removed stale Automerge publisher session.');
        }
      } on Object catch (error) {
        failures.add('game-side cleanup: ${errorMessage(error)}');
      }
    }

    for (final failure in failures) {
      stderr.writeln('UGC cleanup failed ($failure).');
    }
    return failures.isEmpty ? 0 : 1;
  }

  UgcLiveSyncSettings _withoutTransientUgcState(UgcLiveSyncSettings settings) {
    return UgcLiveSyncSettings(
      transport: settings.transport,
      watchFolder: settings.watchFolder,
      syncServerUrl: settings.syncServerUrl,
      sceneId: settings.sceneId,
      maxSnapshotBytes: settings.maxSnapshotBytes,
      debounceMilliseconds: settings.debounceMilliseconds,
    );
  }

  UgcLiveSyncSettings _withAutoConnect(
    UgcLiveSyncSettings base, {
    String? transport,
    String? watchFolder,
    String? documentUrl,
    String? sceneId,
  }) {
    return UgcLiveSyncSettings(
      transport: transport ?? base.transport,
      watchFolder: watchFolder ?? base.watchFolder,
      editorUrl: base.editorUrl,
      documentUrl: documentUrl ?? base.documentUrl,
      syncServerUrl: base.syncServerUrl,
      sceneId: sceneId ?? base.sceneId,
      autoConnectOnStart: true,
      maxSnapshotBytes: base.maxSnapshotBytes,
      debounceMilliseconds: base.debounceMilliseconds,
    );
  }

  /// Deploys [settings] into the detected install and launches the game — the shared tail of `ugc go-live` and
  /// `ugc dev --launch-game`.
  Future<int> _launchGameWithLiveSync(UgcLiveSyncSettings settings) async {
    final launcher = LocalLauncherRepository();
    final snapshot = await launcher.loadSnapshot();
    final install = snapshot.gameInstall;
    if (install == null) {
      stderr.writeln('No Robotopia install detected.');
      return 1;
    }

    final path = await launcher.deployUgcLiveSyncConfig(install, settings);
    stdout.writeln('Deployed live config to $path (auto-connect on).');

    final profile = snapshot.profiles.firstWhere(
      (item) => item.id == snapshot.selectedProfileId,
      orElse: () => snapshot.profiles.first,
    );
    final result = await launcher.launch(install, profile);
    stdout.writeln(result.message);
    return result.started ? 0 : 1;
  }

  /// Resolves the watch folder for the local-folder channel: explicit flag → the game's advertised default
  /// (status handshake) → `<fallbackRoot>/ugc-watch`. Creates the folder so both sides can start immediately.
  Future<String> _resolveWatchFolder(
    String? explicit, {
    required String fallbackRoot,
    bool create = true,
  }) async {
    var folder = explicit ?? '';
    if (folder.isEmpty) {
      try {
        final launcher = LocalLauncherRepository();
        final install =
            await launcher.detectKnownInstall() ??
            (await launcher.loadSnapshot()).gameInstall;
        if (install != null) {
          final status = await launcher.readUgcLiveSyncStatus(install);
          folder = status?.defaultWatchFolder ?? '';
        }
      } on Object {
        // Best-effort: fall through to the local default.
      }
    }
    if (folder.isEmpty) {
      folder = p.join(fallbackRoot, 'ugc-watch');
      stdout.writeln(
        'No watch folder specified and no game default detected; using $folder.',
      );
    }
    if (create) {
      Directory(folder).createSync(recursive: true);
    }
    return p.normalize(p.absolute(folder));
  }
}
