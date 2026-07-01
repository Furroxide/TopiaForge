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
      stdout.writeln('  robotopia ugc go-live');
      return 0;
    }

    if (sub == 'status') {
      return _ugcStatus(args.skip(1).toList());
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
            'No packages available. Build dist/vpm with tools/pack-unity-packages.ps1.',
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
      case 'new-repo':
        stdout.writeln(
          'Run tools/pack-unity-packages.ps1 to (re)generate dist/vpm/index.json from your com.robotopia.* '
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
    final launcher = LocalLauncherRepository();
    final snapshot = await launcher.loadSnapshot();
    final install = snapshot.gameInstall;
    if (install == null) {
      stderr.writeln('No Robotopia install detected.');
      return 1;
    }

    final workspace = await developerRepository.loadDeveloperWorkspace();
    final base =
        workspace.project?.unityCompanion.liveSync ??
        const UgcLiveSyncSettings();
    final settings = UgcLiveSyncSettings(
      transport: base.transport,
      watchFolder: base.watchFolder,
      editorUrl: base.editorUrl,
      documentUrl: base.documentUrl,
      syncServerUrl: base.syncServerUrl,
      sceneId: base.sceneId,
      autoConnectOnStart: true,
      maxSnapshotBytes: base.maxSnapshotBytes,
      debounceMilliseconds: base.debounceMilliseconds,
    );

    final path = await launcher.deployUgcLiveSyncConfig(install, settings);
    stdout.writeln('Deployed live config to $path (auto-connect on).');
    if (settings.transport == 'automerge' && settings.documentUrl.isEmpty) {
      stdout.writeln(
        'Tip: run `robotopia ugc watch <folder>` to obtain a live document URL, then re-run go-live.',
      );
    }

    final profile = snapshot.profiles.firstWhere(
      (item) => item.id == snapshot.selectedProfileId,
      orElse: () => snapshot.profiles.first,
    );
    final result = await launcher.launch(install, profile);
    stdout.writeln(result.message);
    return result.started ? 0 : 1;
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
