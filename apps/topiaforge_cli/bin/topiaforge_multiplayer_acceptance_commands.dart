part of 'topiaforge.dart';

extension _MultiplayerAcceptanceCommands on _TopiaForgeCli {
  Future<int> _multiplayerAcceptance(List<String> args) async {
    const usage =
        'Usage: topiaforge acceptance run --suite multiplayer '
        '[--dry-run] [--case id ...] [--all] --game-dir source '
        '--isolation-record host-record --guest-isolation-record guest-record '
        '--package probe --output host-evidence [--timeout-seconds 30..3600]';
    if (args.contains('--help')) {
      stdout.writeln(usage);
      return 0;
    }
    final parsed = _parseFlagArguments(
      args,
      {
        '--suite',
        '--case',
        '--game-dir',
        '--isolation-record',
        '--guest-isolation-record',
        '--package',
        '--output',
        '--timeout-seconds',
      },
      {'--dry-run', '--all'},
      usage,
    );
    if (parsed.values['--suite'] != 'multiplayer' ||
        (parsed.flags.contains('--all') && parsed.cases.isNotEmpty)) {
      throw UsageError(usage);
    }
    final root = _findRepoRoot();
    if (root == null) {
      throw UsageError('Run this suite from the source checkout.');
    }
    final spec = MultiplayerAcceptanceSpec.decode(
      File(
        p.join(root, 'tests', 'multiplayer-live-acceptance.json'),
      ).readAsStringSync(),
    );
    final Map<String, Object?> plan;
    try {
      plan = spec.dryRun(parsed.cases);
    } on FormatException catch (error) {
      throw UsageError('${error.message}\n$usage');
    }
    if (parsed.flags.contains('--dry-run')) {
      stdout.writeln(jsonEncode(plan));
      return 0;
    }
    for (final flag in [
      '--game-dir',
      '--isolation-record',
      '--guest-isolation-record',
      '--package',
      '--output',
    ]) {
      if (parsed.values[flag]?.trim().isNotEmpty != true) {
        throw UsageError(usage);
      }
    }
    final seconds = int.tryParse(parsed.values['--timeout-seconds'] ?? '600');
    if (seconds == null || seconds < 30 || seconds > 3600) {
      throw UsageError(usage);
    }
    String path(String flag) => p.normalize(p.absolute(parsed.values[flag]!));
    final runner = MultiplayerAcceptanceRunner(
      command: (args, isolation) => _TopiaForgeCli(
        developerRepository,
        acceptanceIsolation: isolation,
      ).run(args),
    );
    try {
      await runner.run(
        MultiplayerAcceptanceOptions(
          repositoryRoot: root,
          sourceGameRoot: path('--game-dir'),
          hostRecord: path('--isolation-record'),
          guestRecord: path('--guest-isolation-record'),
          outputRoot: path('--output'),
          packagePath: path('--package'),
          timeout: Duration(seconds: seconds),
          caseIds: parsed.cases,
        ),
        spec,
      );
      stdout.writeln(
        'Multiplayer acceptance passed ${spec.select(parsed.cases).length} cases on the listen-pair topology.',
      );
      return 0;
    } on MultiplayerAcceptanceRefusal catch (refusal) {
      // The harness's own fixed sentence, with its stage and next step.
      stderr.writeln(refusal.message);
      return 1;
    } on Object {
      // Admission errors can contain private QA roots and OS identities.
      stderr.writeln(
        'TFMPACCEPT140: The listen-pair run could not be verified. '
        'Check both isolated QA records, the multiplayer probe package and '
        'owned process cleanup; preserve the private evidence before retrying.',
      );
      return 1;
    }
  }
}
