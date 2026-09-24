part of 'topiaforge.dart';

extension _LaunchCommands on _TopiaForgeCli {
  CliLaunchOptions _parseLaunchOptions(
    List<String> args, {
    Set<String> extraValues = const {},
    Set<String> extraSwitches = const {},
  }) {
    try {
      return CliLaunchOptions.parse(
        args,
        extraValues: extraValues,
        extraSwitches: extraSwitches,
      );
    } on FormatException catch (error) {
      throw UsageError(
        '${error.message}\nUsage: topiaforge launch|restart '
        '[--game-dir path] [--profile id] [--target id [--world id] '
        '[--transition scene-replacement|additive-arena] | --main-menu] [--no-wait | --wait-seconds 1..300]',
      );
    }
  }

  LauncherProfile _launchProfile(
    LauncherSnapshot snapshot,
    CliLaunchOptions options,
  ) {
    final id = options.profileId ?? snapshot.selectedProfileId;
    final profile = snapshot.profiles
        .where((profile) => profile.id == id)
        .firstOrNull;
    if (profile == null) {
      throw StateError(
        'Profile "$id" is unavailable. Select an existing profile in the launcher or pass --profile <id>.',
      );
    }
    return profile;
  }

  Future<int> _launch(List<String> args, {required bool restart}) async {
    final options = _parseLaunchOptions(args);
    return _launchWithOptions(options, restart: restart);
  }

  Future<int> _launchWithOptions(
    CliLaunchOptions options, {
    bool restart = false,
  }) async {
    final launcher = LocalLauncherRepository(
      knownGamePath: options.gameDirectory,
    );
    try {
      final snapshot = await launcher.loadSnapshot();
      final install = options.gameDirectory == null
          ? snapshot.gameInstall
          : await launcher.detectKnownInstall();
      if (install == null) throw StateError(_noInstallRemedy);
      return await _runLaunchWorkflow(
        launcher,
        install,
        _launchProfile(snapshot, options),
        options,
        restart: restart,
      );
    } finally {
      await launcher.dispose();
    }
  }

  Future<int> _runLaunchWorkflow(
    LauncherRepository launcher,
    GameInstall install,
    LauncherProfile profile,
    CliLaunchOptions options, {
    bool restart = false,
  }) async {
    final cancelled = Completer<void>();
    StreamSubscription<ProcessSignal>? signal;
    try {
      signal = ProcessSignal.sigint.watch().listen((_) {
        if (!cancelled.isCompleted) cancelled.complete();
      });
    } on Object {
      // Some hosts do not expose signal streams; the bounded wait still applies.
    }
    try {
      return await runCliLaunch(
        repository: launcher,
        install: install,
        profile: profile,
        options: options,
        restart: restart,
        write: stdout.writeln,
        cancelled: cancelled.future,
      );
    } finally {
      await signal?.cancel();
    }
  }
}
