part of 'robotopia.dart';

extension _RobotopiaEnvironmentCommands on _RobotopiaCli {
  Future<int> _doctor(List<String> args) async {
    final env = await developerRepository.checkEnvironment();
    _printEnvironment(env);

    final report = await developerRepository.runDoctor(
      projectPath: _option(args, '--project'),
    );
    stdout.writeln('');
    stdout.writeln('Project:');
    for (final message in report.messages) {
      stdout.writeln('  $message');
    }
    _printIssues(report.issues);

    stdout.writeln('');
    stdout.writeln('Game compatibility:');
    final compat = await _runGameCompat();
    if (compat == null) {
      stdout.writeln(
        '  (skipped — build the checker: dotnet build src/Robotopia.GameCompat.Extractor -c Release)',
      );
    } else {
      for (final line in (compat.stdout as String).trimRight().split('\n')) {
        stdout.writeln('  ${line.trimRight()}');
      }
    }

    final strict = args.contains('--strict');
    if (strict && !env.developerReady) {
      stderr.writeln(
        'Developer toolchain is not ready (run `robotopia setup`).',
      );
    }
    return report.ok && (!strict || env.developerReady) ? 0 : 1;
  }

  Future<int> _compat(List<String> args) async {
    final result = await _runGameCompat(
      managed: _option(args, '--managed'),
      json: args.contains('--json'),
    );
    if (result == null) {
      stderr.writeln('Could not run the GameCompat extractor.');
      stderr.writeln(
        '  Build it: dotnet build src/Robotopia.GameCompat.Extractor -c Release',
      );
      return 1;
    }
    stdout.write(result.stdout);
    final err = result.stderr as String;
    if (err.isNotEmpty) {
      stderr.write(err);
    }
    return result.exitCode;
  }

  Future<ProcessResult?> _runGameCompat({
    String? managed,
    bool json = false,
  }) async {
    final root = _findRepoRoot();
    if (root == null) {
      return null;
    }
    final verifyArgs = <String>[
      'verify',
      if (managed != null) ...['--managed', managed],
      '--format',
      json ? 'json' : 'text',
    ];

    for (final config in ['Release', 'Debug']) {
      final exe =
          '$root/src/Robotopia.GameCompat.Extractor/bin/$config/net8.0/Robotopia.GameCompat.Extractor.exe';
      if (File(exe).existsSync()) {
        return Process.run(exe, verifyArgs, workingDirectory: root);
      }
    }

    try {
      return await Process.run('dotnet', [
        'run',
        '--project',
        '$root/src/Robotopia.GameCompat.Extractor',
        '-c',
        'Release',
        '--',
        ...verifyArgs,
      ], workingDirectory: root);
    } catch (_) {
      return null;
    }
  }

  String? _findRepoRoot() {
    var dir = Directory.current;
    while (true) {
      if (File('${dir.path}/RobotopiaModManager.slnx').existsSync()) {
        return dir.path;
      }
      final parent = dir.parent;
      if (parent.path == dir.path) {
        return null;
      }
      dir = parent;
    }
  }

  Future<int> _setup(List<String> args) async {
    stdout.writeln('QuantumWorks developer setup');
    stdout.writeln('');

    final result = await developerRepository.runSetup();
    _printEnvironment(result.environment);
    stdout.writeln('');
    for (final action in result.actions) {
      stdout.writeln('- $action');
    }
    for (final issue in result.issues) {
      stderr.writeln('${issue.severity.name}: ${issue.message}');
    }

    stdout.writeln('');
    if (result.environment.developerReady) {
      stdout.writeln(
        'Ready to build mods. Next: robotopia new mod <id> --name "My Mod".',
      );
    } else {
      stdout.writeln(
        'Install the missing developer tools listed above, then re-run `robotopia setup`.',
      );
    }
    stdout.writeln(
      'To only consume mods you need none of this — use the launcher, or `robotopia install <package>` then `robotopia launch`.',
    );
    return result.environment.developerReady ? 0 : 1;
  }

  void _printEnvironment(EnvironmentReport env) {
    stdout.writeln(
      'Consuming mods needs no developer tools — use the launcher, or `robotopia install <package>` then `robotopia launch`.',
    );
    stdout.writeln('');
    stdout.writeln('Build mods (.NET, required to develop):');
    _printChecks(env.ofPurpose(ToolPurpose.develop));
    stdout.writeln('UGC live-sync (optional):');
    _printChecks([
      ...env.ofPurpose(ToolPurpose.ugcUnity),
      ...env.ofPurpose(ToolPurpose.ugcAutomerge),
    ]);
    final other = env.ofPurpose(ToolPurpose.optional).toList();
    if (other.isNotEmpty) {
      stdout.writeln('Other:');
      _printChecks(other);
    }
  }

  void _printChecks(Iterable<ToolCheck> checks) {
    for (final check in checks) {
      final mark = switch (check.status) {
        ToolStatus.ok => 'OK ',
        ToolStatus.outdated => 'OLD',
        ToolStatus.warning => ' ! ',
        ToolStatus.missing => ' X ',
      };
      final detail = check.detail.isEmpty ? '' : ' — ${check.detail}';
      stdout.writeln('  [$mark] ${check.name}$detail');
      if (!check.ok && check.remediation.isNotEmpty) {
        final url = check.url.isEmpty ? '' : ' (${check.url})';
        stdout.writeln('         ${check.remediation}$url');
      }
    }
  }

  Future<bool> _ensureBuildTooling() async {
    final env = await developerRepository.checkEnvironment();
    if (env.developerReady) {
      return true;
    }
    stderr.writeln(
      'Cannot build/pack — developer tooling is missing or outdated:',
    );
    for (final blocker in env.blockers) {
      final url = blocker.url.isEmpty ? '' : ' (${blocker.url})';
      stderr.writeln('  - ${blocker.name}: ${blocker.remediation}$url');
    }
    stderr.writeln('Run `robotopia setup` for setup help.');
    return false;
  }
}
