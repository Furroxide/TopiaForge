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
      final binDir =
          '$root/src/Robotopia.GameCompat.Extractor/bin/$config/net8.0';
      for (final name in [
        'Robotopia.GameCompat.Extractor.exe',
        'Robotopia.GameCompat.Extractor',
      ]) {
        final exe = '$binDir/$name';
        if (File(exe).existsSync()) {
          return Process.run(exe, verifyArgs, workingDirectory: root);
        }
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

  /// Native replacement for the retired tools/install-local.ps1: builds the
  /// loader solution, installs/repairs the BepInEx runtime for the detected
  /// game layout, and stages the template plus every first-party mod in the
  /// game's package-inbox.
  Future<int> _devInstall(List<String> args) async {
    if (!await _ensureBuildTooling()) {
      return 1;
    }
    final repoRoot = _findRepoRoot();
    if (repoRoot == null) {
      throw StateError(
        'The QuantumWorks repository root was not found from '
        '${Directory.current.path}.',
      );
    }
    final configuration = _option(args, '--configuration') ?? 'Release';

    if (!args.contains('--skip-build')) {
      stdout.writeln('Building RobotopiaModManager.slnx ($configuration)...');
      final build = await Process.run('dotnet', [
        'build',
        p.join(repoRoot, 'RobotopiaModManager.slnx'),
        '-c',
        configuration,
      ], workingDirectory: repoRoot);
      if (build.exitCode != 0) {
        stderr.writeln('${build.stdout}\n${build.stderr}'.trim());
        return 1;
      }
    }

    final launcher = LocalLauncherRepository(
      repositoryRoot: repoRoot,
      knownGamePath: _option(args, '--game-dir'),
    );
    final install = await launcher.detectKnownInstall();
    if (install == null) {
      throw StateError(
        'Robotopia install was not detected. Pass --game-dir <path to the '
        'game folder> (on Linux: the Windows-layout game folder inside your '
        'Proton prefix).',
      );
    }

    final report = await launcher.installOrRepairRuntime(install);
    for (final action in report.actions) {
      stdout.writeln('- $action');
    }
    _printIssues(report.issues);
    if (!report.ok) {
      return 1;
    }

    final inbox = p.join(
      install.path,
      'BepInEx',
      'RobotopiaModManager',
      'package-inbox',
    );
    Directory(inbox).createSync(recursive: true);
    Directory(
      p.join(install.path, 'BepInEx', 'RobotopiaModManager', 'logs'),
    ).createSync(recursive: true);

    final staged = await _packAllMods(
      outputDir: inbox,
      configuration: configuration,
      // Dev installs stage everything, including DevTool-category mods.
      includeDevMods: !args.contains('--no-dev-mods'),
    );
    stdout.writeln('');
    stdout.writeln('Installed the QuantumWorks runtime into ${install.path}');
    stdout.writeln('${staged.length} package(s) staged in the package-inbox.');
    if (install.layout == GameInstallLayout.linuxProton) {
      stdout.writeln(
        'Launch Robotopia under Proton/Wine with '
        'WINEDLLOVERRIDES="winhttp=n,b" — staged packages install '
        'automatically at launch.',
      );
    } else {
      stdout.writeln(
        'Launch Robotopia — staged packages install automatically at launch.',
      );
    }
    return 0;
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
