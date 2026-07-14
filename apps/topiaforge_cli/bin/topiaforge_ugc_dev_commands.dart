part of 'topiaforge.dart';

extension _TopiaForgeUgcDevCommands on _TopiaForgeCli {
  /// `topiaforge ugc setup` — persists live-sync settings into the project and deploys the game runtime config
  /// in one shot. Usable without a game install (`--no-deploy` or auto-skip with a warning).
  Future<int> _ugcSetup(List<String> args) async {
    final projectPath = _option(args, '--project') ?? Directory.current.path;
    final workspace = await developerRepository.loadDeveloperWorkspace(
      projectPath: projectPath,
    );
    final base =
        workspace.project?.unityCompanion.liveSync ??
        const UgcLiveSyncSettings();

    var watch = _option(args, '--watch') ?? base.watchFolder;
    final transport = UgcLiveSyncSettings.normalizeTransport(
      _option(args, '--transport') ?? base.transport,
    );
    if (transport == 'localFolder') {
      watch = await _resolveWatchFolder(
        watch.isEmpty ? null : watch,
        fallbackRoot: workspace.projectRoot,
      );
    }
    final settings = UgcLiveSyncSettings(
      transport: transport,
      watchFolder: watch,
      editorUrl: base.editorUrl,
      documentUrl: _option(args, '--doc') ?? base.documentUrl,
      syncServerUrl: _option(args, '--sync') ?? base.syncServerUrl,
      sceneId: _option(args, '--scene') ?? base.sceneId,
      autoConnectOnStart: args.contains('--no-auto-connect')
          ? false
          : (args.contains('--auto-connect') || base.autoConnectOnStart),
      maxSnapshotBytes:
          int.tryParse(_option(args, '--max-snapshot') ?? '') ??
          base.maxSnapshotBytes,
      debounceMilliseconds:
          int.tryParse(_option(args, '--debounce') ?? '') ??
          base.debounceMilliseconds,
    );

    if (workspace.hasProject) {
      await developerRepository.updateUgcLiveSync(
        workspace.projectRoot,
        settings,
      );
      stdout.writeln(
        'Saved live-sync settings to ${workspace.projectRoot} (topiaforge.project.json).',
      );
    } else {
      stdout.writeln(
        'No topiaforge.project.json found at $projectPath; settings were not persisted to a project.',
      );
    }

    if (args.contains('--no-deploy')) {
      return 0;
    }
    final launcher = LocalLauncherRepository();
    final install =
        await launcher.detectKnownInstall() ??
        (await launcher.loadSnapshot()).gameInstall;
    if (install == null) {
      stdout.writeln(
        'No Robotopia install detected — skipped deploying the game config. '
        'Re-run after selecting an install (or pass --no-deploy to silence this).',
      );
      return 0;
    }
    final path = await launcher.deployUgcLiveSyncConfig(install, settings);
    stdout.writeln('Deployed game live-sync config to $path.');
    return 0;
  }

  /// `topiaforge ugc dev` — the one-command live-sync authoring loop: resolve (or create) the Unity world
  /// project, ensure the UGC companion package, seed its live-sync config, deploy the game config, and launch
  /// the Unity editor connected. `--launch-game` completes the loop by starting the game too.
  Future<int> _ugcDev(List<String> args) async {
    final dryRun = args.contains('--dry-run');
    final newName = _option(args, '--new');
    final transport = UgcLiveSyncSettings.normalizeTransport(
      _option(args, '--transport'),
    );

    // 1. Resolve the Unity project.
    String? projectPath;
    if (newName != null) {
      final parent = _option(args, '--dir') ?? Directory.current.path;
      if (dryRun) {
        projectPath = p.join(parent, newName);
        stdout.writeln(
          '[dry-run] Would create Unity world project "$newName" in $parent.',
        );
      } else {
        final projects = await developerRepository.createUnityProject(
          parentDirectory: parent,
          name: newName,
        );
        projectPath = projects
            .firstWhere(
              (project) => p.basename(project.path) == newName,
              orElse: () => projects.last,
            )
            .path;
        stdout.writeln('Created Unity world project at $projectPath.');
      }
    } else {
      projectPath = await _resolveUnityDevProject(_option(args, '--project'));
    }
    if (projectPath == null) {
      stderr.writeln(
        'No Unity world project found. Pass --project <path|name>, run from a Unity project, or scaffold one '
        'with --new <name> (or `topiaforge new unity-world <name>`).',
      );
      return 1;
    }

    // 2-3. Companion package + watch folder.
    final watch = await _resolveWatchFolder(
      _option(args, '--watch'),
      fallbackRoot: projectPath,
      create: !dryRun,
    );
    final settings = _withAutoConnect(
      const UgcLiveSyncSettings(),
      transport: transport,
      watchFolder: watch,
      documentUrl: _option(args, '--doc'),
      sceneId: _option(args, '--scene'),
    );

    if (dryRun) {
      stdout.writeln('[dry-run] Project        : $projectPath');
      stdout.writeln('[dry-run] Watch folder   : $watch');
      stdout.writeln('[dry-run] Transport      : $transport');
      stdout.writeln(
        '[dry-run] Would ensure Packages/io.github.furroxide.topiaforge.ugc-companion, write '
        'ProjectSettings/TopiaForgeUgcCompanion.json, deploy the game config, and launch Unity.',
      );
      final editors = await developerRepository.listUnityEditors();
      stdout.writeln(
        '[dry-run] Unity editors  : ${editors.isEmpty ? '(none found)' : editors.map((e) => e.version).join(', ')}',
      );
      return 0;
    }

    final companionReady = await developerRepository.ensureUgcCompanionPackage(
      projectPath,
      update: args.contains('--update-companion'),
    );
    if (!companionReady) {
      stdout.writeln(
        'Warning: could not install the UGC companion package (repo template missing). '
        'Add Packages/io.github.furroxide.topiaforge.ugc-companion manually.',
      );
    }
    try {
      await developerRepository.resolveUnityProject(projectPath);
    } on Object catch (error) {
      stdout.writeln(
        'Warning: VPM resolve failed ($error). If packages are missing, build the local listing with '
        '`topiaforge unity pack-packages` first.',
      );
    }

    // 4. Seed the companion so the UGC Live Sync window opens configured with live sync ON.
    final seedPath = await developerRepository.writeUgcCompanionSeed(
      projectPath,
      watchFolder: watch,
      projectName: p.basename(projectPath),
      sceneId: _option(args, '--scene') ?? '',
      sceneName: _option(args, '--scene-name') ?? '',
      environment: _option(args, '--environment') ?? '',
    );
    stdout.writeln('Seeded companion live-sync config: $seedPath');

    // 5. Deploy the game-side config (skip with a warning when no install is present).
    var liveSettings = settings;
    final launcher = LocalLauncherRepository();
    final install =
        await launcher.detectKnownInstall() ??
        (await launcher.loadSnapshot()).gameInstall;

    // 6. Automerge channel: start the publisher sidecar and capture the document URL via its session file.
    if (transport == 'automerge') {
      final documentUrl = await _startAutomergePublisher(watch, liveSettings);
      if (documentUrl != null) {
        liveSettings = _withAutoConnect(liveSettings, documentUrl: documentUrl);
      } else {
        liveSettings = UgcLiveSyncTransitions.localFallback(liveSettings);
      }
    }

    if (install == null) {
      stdout.writeln(
        'No Robotopia install detected — skipped deploying the game config. Unity-side live sync still works; '
        'deploy later with `topiaforge ugc setup`.',
      );
    } else {
      final configPath = await launcher.deployUgcLiveSyncConfig(
        install,
        liveSettings,
      );
      stdout.writeln(
        'Deployed game live-sync config to $configPath (auto-connect on).',
      );
    }

    // 7. Launch the Unity editor connected to the loop.
    final editor = await developerRepository.openProjectInUnity(projectPath);
    stdout.writeln('Launched Unity ($editor) with $projectPath.');
    stdout.writeln(
      'The UGC Live Sync window opens preconfigured (watch folder set, Live Sync ON). Set an Export root and '
      'save the scene to publish your first snapshot.',
    );

    // 8. Optionally complete the loop by launching the game against the same settings.
    if (args.contains('--launch-game')) {
      return _launchGameWithLiveSync(liveSettings);
    }
    stdout.writeln(
      'Run `topiaforge ugc dev --launch-game` (or `topiaforge ugc go-live`) to start the game connected.',
    );
    return 0;
  }

  /// Project resolution for `ugc dev`: explicit path → registered-project name → cwd Unity project → the most
  /// recently opened registered Unity world project.
  Future<String?> _resolveUnityDevProject(String? selector) async {
    bool isUnityProject(String path) =>
        Directory(p.join(path, 'ProjectSettings')).existsSync() &&
        Directory(p.join(path, 'Assets')).existsSync();

    if (selector != null) {
      if (Directory(selector).existsSync()) {
        return p.normalize(p.absolute(selector));
      }
      final projects = await developerRepository.listProjects();
      for (final project in projects) {
        if (project.name.toLowerCase() == selector.toLowerCase() &&
            project.isUnity) {
          return project.path;
        }
      }
      return null;
    }

    if (isUnityProject(Directory.current.path)) {
      return Directory.current.path;
    }

    final projects = await developerRepository.listProjects();
    final worlds =
        projects
            .where(
              (project) =>
                  project.kind == ProjectKind.unityWorld &&
                  Directory(project.path).existsSync(),
            )
            .toList()
          ..sort((a, b) => b.lastOpenedUtc.compareTo(a.lastOpenedUtc));
    return worlds.isEmpty ? null : worlds.first.path;
  }

  /// Starts the Automerge publisher sidecar detached (it outlives this CLI call) and polls its session file for
  /// the live document URL. Returns null when the sidecar or Node is unavailable (with a printed warning).
  Future<String?> _startAutomergePublisher(
    String watchFolder,
    UgcLiveSyncSettings settings,
  ) async {
    final sidecar = _findSidecar();
    if (sidecar == null) {
      stdout.writeln(
        'Warning: UGC Automerge sidecar not found (tools/ugc-automerge-sidecar); staying on the local channel.',
      );
      return null;
    }
    final sidecarDir = sidecar.directory;
    final sessionFile = File(
      p.join(developerRepository.developerDataRoot, 'ugc-session.json'),
    );
    if (sessionFile.existsSync()) {
      sessionFile.deleteSync();
    }
    sessionFile.parent.createSync(recursive: true);

    late Process publisher;
    try {
      await sidecar.prepare(requireDependencies: true);
      publisher = await Process.start(
        'node',
        [
          sidecar.scriptPath,
          '--watch',
          watchFolder,
          '--sync',
          settings.syncServerUrl,
          if (settings.sceneId.isNotEmpty) ...['--scene', settings.sceneId],
          '--session-file',
          sessionFile.path,
        ],
        workingDirectory: sidecarDir,
        mode: ProcessStartMode.detached,
      );
    } on Object catch (error) {
      stdout.writeln(
        'Warning: could not start the UGC sidecar ($error); staying on the local channel. Install Node 20+.',
      );
      return null;
    }

    stdout.writeln(
      'Started Automerge publisher; waiting for the document URL...',
    );
    var malformedReads = 0;
    for (var attempt = 0; attempt < 60; attempt++) {
      await Future<void>.delayed(const Duration(seconds: 1));
      if (!sessionFile.existsSync()) {
        continue;
      }
      try {
        final session = readBoundedJsonObjectSync(
          sessionFile,
          maxBytes: CliFileLimits.session,
        );
        final documentUrl = (session['documentUrl'] as String?) ?? '';
        final publisherPid = (session['publisherPid'] as num?)?.toInt() ?? 0;
        final leaseToken =
            (session['publisherLeaseToken'] as String?)?.trim() ?? '';
        if (publisherPid != publisher.pid || leaseToken.isEmpty) {
          stdout.writeln(
            'Warning: the publisher session was replaced or malformed; staying on the local channel.',
          );
          await _stopDetachedPublisher(publisher);
          return null;
        }
        if (documentUrl.isNotEmpty) {
          stdout.writeln('Live document: $documentUrl');
          return documentUrl;
        }
      } on Object {
        malformedReads += 1;
        if (malformedReads >= 3) {
          stdout.writeln(
            'Warning: the publisher wrote an invalid session file; staying on the local channel.',
          );
          await _stopDetachedPublisher(publisher);
          return null;
        }
      }
    }
    stdout.writeln(
      'Warning: the publisher did not report a document URL within 60s; the game config keeps the local channel. '
      'Check `topiaforge ugc check` and re-run.',
    );
    await _stopDetachedPublisher(publisher);
    return null;
  }

  Future<void> _stopDetachedPublisher(Process publisher) async {
    publisher.kill(ProcessSignal.sigkill);
    await Future<void>.delayed(const Duration(milliseconds: 100));
    // Do not compare-then-delete the shared lease here: a replacement can land
    // between those operations. The dead PID makes this stale lease invalid,
    // and an explicit cleanup or the next publisher safely revokes it.
  }
}
