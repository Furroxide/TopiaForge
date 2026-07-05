part of 'robotopia.dart';

extension _RobotopiaUgcCommands on _RobotopiaCli {
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
      stdout.writeln('      [--update-companion] [--launch-game] [--dry-run]');
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
}

String? _findSidecar() {
  var dir = Directory.current.absolute;
  while (true) {
    final candidate = File('${dir.path}/tools/ugc-automerge-sidecar/index.mjs');
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
