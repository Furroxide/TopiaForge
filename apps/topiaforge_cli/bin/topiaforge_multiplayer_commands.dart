part of 'topiaforge.dart';

const _hostUsage =
    'Usage: topiaforge host [--game-dir path] [--profile id] '
    '[--target id | --main-menu] [--name text] [--visibility lan|private|invite|public] '
    '[--port 0..65535] [--max-players 2..16] [--password] [--serve-packages] '
    '[--allow-direct] [--yes] [--wait-seconds 1..300]';
const _serversUsage =
    'Usage: topiaforge servers [--source all|internet|lan] '
    '[--game-build build] [--protocol-major number] [--after server-id] '
    '[--wait-seconds 0..30] [--json]';

extension _MultiplayerCommands on _TopiaForgeCli {
  Future<int> _servers(List<String> args) async {
    if (args.contains('--help')) {
      stdout.writeln(_serversUsage);
      return 0;
    }
    final CliServersOptions options;
    try {
      options = CliServersOptions(args);
    } on FormatException catch (error) {
      throw UsageError('${error.message}\n$_serversUsage');
    }
    final repository = LocalMultiplayerRepository(
      dataRoot: resolveTopiaForgeDataRoot(),
    );
    try {
      return await runCliServers(
        repository: repository,
        options: options,
        write: stdout.writeln,
        notice: stderr.writeln,
        resolveGameBuild: () async {
          final launcher = LocalLauncherRepository();
          try {
            final install = await launcher.detectKnownInstall();
            final version = install?.gameVersion;
            return version == null
                ? null
                : RegExp(r'^0\.0\.([0-9]+)$').firstMatch(version)?.group(1);
          } finally {
            await launcher.dispose();
          }
        },
      );
    } finally {
      await repository.dispose();
    }
  }

  Future<int> _host(List<String> args) async {
    if (args.contains('--help')) {
      stdout.writeln(_hostUsage);
      stdout.writeln(
        'Without a terminal --yes is required. With a terminal, every consent still asks. Direct play needs --allow-direct for this session. Passwords are typed in game, so --password waits up to 300 seconds unless --wait-seconds is given.',
      );
      return 0;
    }
    final CliHostOptions options;
    try {
      options = CliHostOptions(args);
    } on FormatException {
      // Raw option text can contain a mistyped address; never echo it.
      stderr.writeln(
        'TFHOST100: Invalid host options. Review the supported flags.\n$_hostUsage',
      );
      return 2;
    }
    if (!stdin.hasTerminal && !options.yes) {
      stderr.writeln(
        'TFHOST110: Hosting needs consent. Read topiaforge host --help and rerun with --yes when there is no terminal.',
      );
      return 2;
    }
    final launcher = LocalLauncherRepository(
      knownGamePath: options.launch.gameDirectory,
    );
    final multiplayer = LocalMultiplayerRepository(dataRoot: launcher.dataRoot);
    final cancelled = Completer<void>();
    StreamSubscription<ProcessSignal>? signal;
    try {
      try {
        signal = ProcessSignal.sigint.watch().listen((_) {
          if (!cancelled.isCompleted) cancelled.complete();
        });
      } on Object {
        /* Bounded wait also covers hosts without signals. */
      }
      final snapshot = await launcher.loadSnapshot();
      final install = options.launch.gameDirectory == null
          ? snapshot.gameInstall
          : await launcher.detectKnownInstall();
      if (install == null) {
        stderr.writeln(
          'TFHOST100: Robotopia was not found. Select its install in Setup or pass --game-dir.',
        );
        return 1;
      }
      final profile = _launchProfile(snapshot, options.launch);
      return await runCliHost(
        launcher: launcher,
        multiplayer: multiplayer,
        install: install,
        profile: profile,
        developerMode: snapshot.developerMode,
        options: options,
        hasTerminal: stdin.hasTerminal,
        confirm: (prompt) async {
          stdout.writeln(prompt);
          return stdin.readLineSync()?.trim().toLowerCase() == 'y';
        },
        write: stdout.writeln,
        cancelled: cancelled.future,
        serviceOrigin: MultiplayerServiceConfiguration.fromEnvironment().origin
            ?.toString(),
        launch: (request) => launcher.launchSession(
          install,
          profile,
          request,
          selectionOverride: options.launch.selectionOverride,
        ),
      );
    } on Object {
      stderr.writeln(
        'TFHOST100: Hosting setup could not be read. Check the selected install and profile in the launcher, then retry.',
      );
      return 1;
    } finally {
      await signal?.cancel();
      await multiplayer.dispose();
      await launcher.dispose();
    }
  }
}
