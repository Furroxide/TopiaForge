part of 'robotopia.dart';

extension _RobotopiaUgcUnityCommands on _RobotopiaCli {
  // Drives the UGC Automerge sidecar. The local-folder channel needs none of
  // this; this is for full web-editor parity and remote collaboration.
  Future<int> _ugc(List<String> args) async {
    final sub = args.firstOrNull;
    if (sub == null || sub == 'help' || sub == '--help') {
      stdout.writeln('Usage:');
      stdout.writeln(
        '  robotopia ugc publish --file <project.json> [--sync url] [--doc url] [--scene id] [--session-file path]',
      );
      stdout.writeln(
        '  robotopia ugc watch <folder> [--sync url] [--doc url] [--scene id] [--session-file path]',
      );
      stdout.writeln('  robotopia ugc check [--watch folder] [--sync url]');
      stdout.writeln('  robotopia ugc status [--watch folder]');
      stdout.writeln(
        '  robotopia ugc setup [--transport localFolder|automerge] [--watch folder] [--sync url] [--doc url]',
      );
      stdout.writeln(
        '      [--scene id] [--auto-connect|--no-auto-connect] [--debounce ms] [--max-snapshot bytes]',
      );
      stdout.writeln('      [--project path] [--no-deploy]');
      stdout.writeln(
        '  robotopia ugc dev [--project path|name] [--new name [--dir path]] [--watch folder]',
      );
      stdout.writeln(
        '      [--scene id] [--scene-name n] [--environment env] [--transport localFolder|automerge]',
      );
      stdout.writeln(
        '      [--update-companion] [--launch-game] [--dry-run]',
      );
      stdout.writeln('  robotopia ugc go-live');
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

    final sidecar = _findSidecar();
    if (sidecar == null) {
      stderr.writeln(
        'UGC Automerge sidecar not found (tools/ugc-automerge-sidecar/index.mjs). Run from the repo.',
      );
      return 1;
    }
    final sidecarDir = File(sidecar).parent.path;

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
          stderr.writeln('Usage: robotopia ugc watch <folder> [...]');
          return 1;
        }
        forward.addAll(['--watch', folder, ...args.skip(2)]);
        break;
      default:
        stderr.writeln('Unknown ugc subcommand: $sub');
        return 1;
    }

    try {
      if (!Directory('$sidecarDir/node_modules').existsSync() &&
          sub != 'check') {
        stdout.writeln('Installing sidecar dependencies (npm install)...');
        final install = await Process.start(
          'npm',
          ['install', '--no-fund', '--no-audit'],
          workingDirectory: sidecarDir,
          mode: ProcessStartMode.inheritStdio,
          runInShell: true,
        );
        final installCode = await install.exitCode;
        if (installCode != 0) {
          stderr.writeln('npm install failed (exit $installCode).');
          return installCode;
        }
      }

      final process = await Process.start(
        'node',
        [sidecar, ...forward],
        mode: ProcessStartMode.inheritStdio,
        runInShell: true,
      );
      return process.exitCode;
    } on ProcessException catch (error) {
      stderr.writeln(
        'Could not run Node.js (${error.message}). Install Node 20+ and retry.',
      );
      return 1;
    }
  }

  // Unity-side VPM from the terminal: scaffold packages, manage packages, and
  // manage listings.
  Future<int> _unity(List<String> args) async {
    final sub = args.firstOrNull;
    String pathArg(int index) =>
        args.length > index && !args[index].startsWith('-')
        ? args[index]
        : Directory.current.path;

    switch (sub) {
      case 'pack-packages':
        final summary = await developerRepository.packUnityPackages(
          outputDir: _option(args, '--output') ?? '',
        );
        summary.forEach(stdout.writeln);
        return 0;
      case 'new-package':
        if (args.length < 2) {
          stderr.writeln(
            'Usage: robotopia unity new-package <id> [--name Name] [--dir path]',
          );
          return 1;
        }
        final path = await developerRepository.createUnityPackage(
          parentDirectory: _option(args, '--dir') ?? Directory.current.path,
          id: args[1],
          name: _option(args, '--name') ?? '',
        );
        stdout.writeln('Created package ${args[1]} at $path.');
        return 0;
      case 'resolve':
        final resolved = await developerRepository.resolveUnityProject(
          pathArg(1),
          restore: !args.contains('--no-restore'),
        );
        stdout.writeln('Resolved ${resolved.length} package(s):');
        for (final pkg in resolved) {
          stdout.writeln('  ${pkg.id} ${pkg.version}');
        }
        return 0;
      case 'add':
        if (args.length < 2) {
          stderr.writeln('Usage: robotopia unity add <id[@range]> [path]');
          return 1;
        }
        final spec = args[1];
        final at = spec.indexOf('@');
        final id = at < 0 ? spec : spec.substring(0, at);
        final range = at < 0 ? '*' : spec.substring(at + 1);
        final resolved = await developerRepository.addUnityPackage(
          pathArg(2),
          id,
          range,
        );
        stdout.writeln(
          'Added $id ($range); ${resolved.length} package(s) resolved.',
        );
        return 0;
      case 'remove':
        if (args.length < 2) {
          stderr.writeln('Usage: robotopia unity remove <id> [path]');
          return 1;
        }
        await developerRepository.removeUnityPackage(pathArg(2), args[1]);
        stdout.writeln('Removed ${args[1]}.');
        return 0;
      case 'list':
        final available = await developerRepository
            .listAvailableUnityPackages();
        if (available.isEmpty) {
          stdout.writeln(
            'No packages available. Build dist/vpm with `robotopia unity pack-packages`.',
          );
        }
        for (final info in available) {
          stdout.writeln('  ${info.name} ${info.version} — ${info.label}');
        }
        return 0;
      case 'repos':
        for (final repo in await developerRepository.listUnityRepos()) {
          stdout.writeln(
            '  ${repo.enabled ? '[x]' : '[ ]'} ${repo.id} ${repo.url}',
          );
        }
        return 0;
      case 'add-repo':
        if (args.length < 2) {
          stderr.writeln('Usage: robotopia unity add-repo <index.json url>');
          return 1;
        }
        final repos = await developerRepository.addUnityRepo(args[1]);
        stdout.writeln('Subscribed; ${repos.length} repo(s).');
        return 0;
      case 'remove-repo':
        if (args.length < 2) {
          stderr.writeln('Usage: robotopia unity remove-repo <id>');
          return 1;
        }
        final repos = await developerRepository.removeUnityRepo(args[1]);
        stdout.writeln('Unsubscribed; ${repos.length} repo(s).');
        return 0;
      case 'build-ui-bundle':
        return _unityBuildUiBundle(args.skip(1).toList());
      case 'new-repo':
        stdout.writeln(
          'Run `robotopia unity pack-packages` to (re)generate dist/vpm/index.json from your com.robotopia.* '
          'packages, then subscribe with `robotopia unity add-repo <path-to-index.json>`.',
        );
        return 0;
      default:
        stdout.writeln('Usage:');
        stdout.writeln(
          '  robotopia unity new-package <id> [--name Name] [--dir path]',
        );
        stdout.writeln('  robotopia unity resolve [path] [--no-restore]');
        stdout.writeln('  robotopia unity add <id[@range]> [path]');
        stdout.writeln('  robotopia unity remove <id> [path]');
        stdout.writeln('  robotopia unity list');
        stdout.writeln(
          '  robotopia unity repos | add-repo <url> | remove-repo <id> | new-repo',
        );
        stdout.writeln(
          '  robotopia unity build-ui-bundle [--unity <editor>] [--rebuild] [--dry-run]',
        );
        return sub == null ? 0 : 1;
    }
  }

  // VCC-style multi-project registry from the terminal: list, add, remove, open.
  Future<int> _projects(List<String> args) async {
    final sub = args.firstOrNull;
    switch (sub) {
      case 'list':
        final projects = await developerRepository.listProjects();
        if (projects.isEmpty) {
          stdout.writeln('No projects tracked.');
        } else {
          for (final project in projects) {
            final unity = project.unityVersion.isEmpty
                ? ''
                : ' [Unity ${project.unityVersion}]';
            stdout.writeln(
              '${project.kind.name}: ${project.name}$unity — ${project.path}',
            );
          }
        }
        final editors = await developerRepository.listUnityEditors();
        if (editors.isNotEmpty) {
          stdout.writeln(
            'Unity editors: ${editors.map((e) => e.version).join(', ')}',
          );
        }
        return 0;
      case 'add':
        final path = args.length > 1 ? args[1] : Directory.current.path;
        final projects = await developerRepository.addExistingProject(path);
        stdout.writeln('Tracked $path (${projects.length} project(s) total).');
        return 0;
      case 'remove':
        if (args.length < 2) {
          stderr.writeln('Usage: robotopia projects remove <path>');
          return 1;
        }
        final projects = await developerRepository.removeProject(args[1]);
        stdout.writeln('Untracked ${args[1]} (${projects.length} remaining).');
        return 0;
      case 'open':
        final path = args.length > 1 ? args[1] : Directory.current.path;
        final editor = await developerRepository.openProjectInUnity(path);
        stdout.writeln('Opened $path in Unity ($editor).');
        return 0;
      default:
        stdout.writeln('Usage:');
        stdout.writeln('  robotopia projects list');
        stdout.writeln('  robotopia projects add [path]');
        stdout.writeln('  robotopia projects remove <path>');
        stdout.writeln('  robotopia projects open [path]');
        return sub == null ? 0 : 1;
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
      final scenes = await launcher.listWatchFolderScenes(folder);
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
    final settings = _withAutoConnect(base);
    if (settings.transport == 'automerge' && settings.documentUrl.isEmpty) {
      stdout.writeln(
        'Tip: run `robotopia ugc watch <folder>` to obtain a live document URL, then re-run go-live.',
      );
    }
    return _launchGameWithLiveSync(settings);
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

  /// `robotopia ugc setup` — persists live-sync settings into the project and deploys the game runtime config
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
        'Saved live-sync settings to ${workspace.projectRoot} (robotopia.project.json).',
      );
    } else {
      stdout.writeln(
        'No robotopia.project.json found at $projectPath; settings were not persisted to a project.',
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

  /// `robotopia ugc dev` — the one-command live-sync authoring loop: resolve (or create) the Unity world
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
        stdout.writeln('[dry-run] Would create Unity world project "$newName" in $parent.');
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
        'with --new <name> (or `robotopia new unity-world <name>`).',
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
        '[dry-run] Would ensure Packages/com.robotopia.ugc-companion, write '
        'ProjectSettings/RobotopiaUgcCompanion.json, deploy the game config, and launch Unity.',
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
        'Add Packages/com.robotopia.ugc-companion manually.',
      );
    }
    try {
      await developerRepository.resolveUnityProject(projectPath);
    } on Object catch (error) {
      stdout.writeln(
        'Warning: VPM resolve failed ($error). If packages are missing, build the local listing with '
        '`robotopia unity pack-packages` first.',
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
      }
    }

    if (install == null) {
      stdout.writeln(
        'No Robotopia install detected — skipped deploying the game config. Unity-side live sync still works; '
        'deploy later with `robotopia ugc setup`.',
      );
    } else {
      final configPath = await launcher.deployUgcLiveSyncConfig(
        install,
        liveSettings,
      );
      stdout.writeln('Deployed game live-sync config to $configPath (auto-connect on).');
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
      'Run `robotopia ugc dev --launch-game` (or `robotopia ugc go-live`) to start the game connected.',
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
    final sidecarDir = File(sidecar).parent.path;
    final sessionFile = File(
      p.join(developerRepository.developerDataRoot, 'ugc-session.json'),
    );
    if (sessionFile.existsSync()) {
      sessionFile.deleteSync();
    }
    sessionFile.parent.createSync(recursive: true);

    try {
      if (!Directory(p.join(sidecarDir, 'node_modules')).existsSync()) {
        stdout.writeln('Installing sidecar dependencies (npm install)...');
        final install = await Process.run(
          'npm',
          ['install', '--no-fund', '--no-audit'],
          workingDirectory: sidecarDir,
          runInShell: true,
        );
        if (install.exitCode != 0) {
          stdout.writeln(
            'Warning: npm install failed (exit ${install.exitCode}); staying on the local channel.',
          );
          return null;
        }
      }
      await Process.start('node', [
        sidecar,
        '--watch',
        watchFolder,
        '--sync',
        settings.syncServerUrl,
        if (settings.sceneId.isNotEmpty) ...['--scene', settings.sceneId],
        '--session-file',
        sessionFile.path,
      ], mode: ProcessStartMode.detached, runInShell: true);
    } on ProcessException catch (error) {
      stdout.writeln(
        'Warning: could not run Node.js (${error.message}); staying on the local channel. Install Node 20+.',
      );
      return null;
    }

    stdout.writeln('Started Automerge publisher; waiting for the document URL...');
    for (var attempt = 0; attempt < 60; attempt++) {
      await Future<void>.delayed(const Duration(seconds: 1));
      if (!sessionFile.existsSync()) {
        continue;
      }
      try {
        final session =
            jsonDecode(await sessionFile.readAsString())
                as Map<String, Object?>;
        final documentUrl = (session['documentUrl'] as String?) ?? '';
        if (documentUrl.isNotEmpty) {
          stdout.writeln('Live document: $documentUrl');
          return documentUrl;
        }
      } on Object {
        // Partial write — retry.
      }
    }
    stdout.writeln(
      'Warning: the publisher did not report a document URL within 60s; the game config keeps the local channel. '
      'Check `robotopia ugc check` and re-run.',
    );
    return null;
  }

  String? _findSidecar() {
    var dir = Directory.current.absolute;
    while (true) {
      final candidate = File(
        '${dir.path}/tools/ugc-automerge-sidecar/index.mjs',
      );
      if (candidate.existsSync()) {
        return candidate.path;
      }
      final parent = dir.parent;
      if (parent.path == dir.path) {
        return null;
      }
      dir = parent;
    }
  }
}
