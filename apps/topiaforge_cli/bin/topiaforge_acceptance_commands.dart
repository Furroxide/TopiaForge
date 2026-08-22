part of 'topiaforge.dart';

extension _TopiaForgeAcceptanceCommands on _TopiaForgeCli {
  static const _acceptanceUsage =
      'Usage: topiaforge acceptance run [--game-dir path] [--package path] '
      '[--output dir] [--case id ...] [--all] [--timeout-seconds 30..3600] '
      '[--skip-runtime-install] [--skip-launch]';

  Future<int> _acceptance(List<String> args) async {
    if (args.firstOrNull != 'run') {
      throw UsageError(_acceptanceUsage);
    }
    final runArgs = args.skip(1).toList(growable: false);
    if (runArgs.contains('--help')) {
      stdout.writeln(_acceptanceUsage);
      stdout.writeln(
        'Release journey: --dev-cli path --dev-project path '
        '--required-loaded-package id --required-log-marker text',
      );
      stdout.writeln(
        'With no --case, every case in tests/live-game-acceptance.json is '
        'required. --all makes that CI requirement explicit.',
      );
      return 0;
    }
    final parsed = _parseAcceptanceArguments(runArgs);
    final repoRoot = _findRepoRoot() ?? Directory.current.absolute.path;
    final gameDirectory = parsed.values['--game-dir']?.trim().isNotEmpty == true
        ? parsed.values['--game-dir']!
        : Platform.environment['ROBOTOPIA_GAME_DIR'] ?? '';
    final timeoutSeconds = int.tryParse(
      parsed.values['--timeout-seconds'] ?? '600',
    );
    if (timeoutSeconds == null ||
        timeoutSeconds < 30 ||
        timeoutSeconds > 3600) {
      throw UsageError(
        '--timeout-seconds must be an integer from 30 through 3600.\n'
        '$_acceptanceUsage',
      );
    }
    String absoluteIfPresent(String flag) {
      final value = parsed.values[flag] ?? '';
      return value.trim().isEmpty ? '' : p.normalize(p.absolute(value));
    }

    final output = parsed.values['--output']?.trim().isNotEmpty == true
        ? p.normalize(p.absolute(parsed.values['--output']!))
        : p.join(Directory.systemTemp.path, 'topiaforge-game-acceptance');
    final options = LiveAcceptanceOptions(
      repositoryRoot: repoRoot,
      gameDirectory: gameDirectory.trim().isEmpty
          ? ''
          : p.normalize(p.absolute(gameDirectory)),
      packagePath: absoluteIfPresent('--package'),
      outputDirectory: output,
      requiredCases: parsed.cases,
      timeout: Duration(seconds: timeoutSeconds),
      devCliPath: absoluteIfPresent('--dev-cli'),
      devProjectPath: absoluteIfPresent('--dev-project'),
      requiredLoadedPackageId: parsed.values['--required-loaded-package'] ?? '',
      requiredLogMarker: parsed.values['--required-log-marker'] ?? '',
      requireAll: parsed.flags.contains('--all'),
      skipRuntimeInstall: parsed.flags.contains('--skip-runtime-install'),
      skipLaunch: parsed.flags.contains('--skip-launch'),
    );
    final runner = LiveAcceptanceRunner(
      commandRunner: (arguments) => run(arguments),
    );
    final evidence = await runner.run(options);
    stdout.writeln(
      'TopiaForge live acceptance passed '
      '${evidence.requiredCases.length} required cases.',
    );
    stdout.writeln('Evidence: ${p.join(output, 'acceptance-result.json')}');
    return 0;
  }

  _AcceptanceArguments _parseFlagArguments(
    List<String> args,
    Set<String> valueFlags,
    Set<String> booleanFlags,
    String usage,
  ) {
    final values = <String, String>{};
    final cases = <String>[];
    final flags = <String>{};
    for (var index = 0; index < args.length; index++) {
      final argument = args[index];
      if (booleanFlags.contains(argument)) {
        flags.add(argument == '--require-all' ? '--all' : argument);
        continue;
      }
      if (!valueFlags.contains(argument) ||
          index + 1 >= args.length ||
          valueFlags.contains(args[index + 1]) ||
          booleanFlags.contains(args[index + 1])) {
        throw UsageError('Unknown or incomplete option: $argument\n$usage');
      }
      final value = args[++index];
      if (argument == '--case') {
        cases.add(value);
      } else {
        values[argument] = value;
      }
    }
    return _AcceptanceArguments(values, cases, flags);
  }

  _AcceptanceArguments _parseAcceptanceArguments(List<String> args) {
    const valueFlags = {
      '--game-dir',
      '--package',
      '--output',
      '--case',
      '--timeout-seconds',
      '--dev-cli',
      '--dev-project',
      '--required-loaded-package',
      '--required-log-marker',
    };
    const booleanFlags = {
      '--all',
      '--require-all',
      '--skip-runtime-install',
      '--skip-launch',
    };
    return _parseFlagArguments(
      args,
      valueFlags,
      booleanFlags,
      _acceptanceUsage,
    );
  }
}

final class _AcceptanceArguments {
  const _AcceptanceArguments(this.values, this.cases, this.flags);

  final Map<String, String> values;
  final List<String> cases;
  final Set<String> flags;
}
