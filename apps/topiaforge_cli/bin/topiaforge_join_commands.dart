part of 'topiaforge.dart';

const _joinUsage =
    'Usage: topiaforge join <code|link|host:port> '
    '[--game-dir path] [--yes] [--allow-direct] [--allow-host-packages] '
    '[--wait-seconds 1..300]';

extension _JoinCommands on _TopiaForgeCli {
  Future<int> _join(List<String> args) async {
    if (args.contains('--help')) {
      stdout.writeln(_joinUsage);
      stdout.writeln(
        'Without a terminal --yes is required. With a terminal, every consent still asks. '
        'Direct play needs --allow-direct for this session. Mods from the host need developer mode and '
        '--allow-host-packages. Passwords are typed only in Robotopia. The default confirmation wait is 300 seconds.',
      );
      return 0;
    }
    final CliJoinOptions options;
    try {
      options = CliJoinOptions(args);
    } on FormatException {
      stderr.writeln(
        'TFJOIN100: Invalid join options. Review the supported flags.\n$_joinUsage',
      );
      return 2;
    }
    final interactive = hasInteractiveInput();
    if (!interactive && !options.yes) {
      stderr.writeln(
        'TFJOIN100: Joining needs consent. Read topiaforge join --help and rerun with --yes when there is no terminal.',
      );
      return 2;
    }
    final launcher = LocalLauncherRepository(
      knownGamePath: options.gameDirectory,
    );
    final multiplayer = LocalMultiplayerRepository(
      dataRoot: launcher.dataRoot,
      content: launcher,
    );
    final cancelled = Completer<void>();
    StreamSubscription<ProcessSignal>? signal;
    try {
      try {
        signal = ProcessSignal.sigint.watch().listen((_) {
          if (!cancelled.isCompleted) cancelled.complete();
        });
      } on Object {
        /* The confirmation wait is bounded without signals too. */
      }
      final snapshot = await launcher.loadSnapshot();
      final install = options.gameDirectory == null
          ? snapshot.gameInstall
          : await launcher.detectKnownInstall();
      if (install == null) {
        stderr.writeln(
          'TFJOIN100: Robotopia was not found. Select its install in Setup or pass --game-dir.',
        );
        return 1;
      }
      return await runCliJoin(
        launcher: launcher,
        multiplayer: multiplayer,
        install: install,
        profileIds: snapshot.profiles.map((profile) => profile.id).toSet(),
        developerMode: snapshot.developerMode,
        options: options,
        hasTerminal: interactive,
        confirm: (prompt) async {
          stdout.writeln(prompt);
          return stdin.readLineSync()?.trim().toLowerCase() == 'y';
        },
        write: stdout.writeln,
        cancelled: cancelled.future,
      );
    } on Object {
      stderr.writeln(
        'TFJOIN100: Join setup could not be read. Check the selected install in the launcher, then retry.',
      );
      return 1;
    } finally {
      await signal?.cancel();
      await multiplayer.dispose();
      await launcher.dispose();
    }
  }
}
