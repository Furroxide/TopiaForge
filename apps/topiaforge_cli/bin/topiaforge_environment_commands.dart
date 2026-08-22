part of 'topiaforge.dart';

extension _TopiaForgeEnvironmentCommands on _TopiaForgeCli {
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
        '  (skipped — the packaged GameCompat checker was not found; reinstall TopiaForge or build it from source)',
      );
    } else {
      for (final line in (compat.stdout as String).trimRight().split('\n')) {
        stdout.writeln('  ${line.trimRight()}');
      }
    }

    stdout.writeln('');
    stdout.writeln('Recommended actions:');
    final recommendations = await _doctorRecommendations(env, report);
    if (recommendations.isEmpty) {
      stdout.writeln('  No action needed.');
    } else {
      for (final recommendation in recommendations) {
        stdout.writeln('  - $recommendation');
      }
    }

    final strict = args.contains('--strict');
    if (strict && !env.developerReady) {
      stderr.writeln(
        'Developer toolchain is not ready (run `topiaforge setup`).',
      );
    }
    return report.ok && (!strict || env.developerReady) ? 0 : 1;
  }

  /// Maps doctor findings to next steps, from structured data only (never
  /// message-string matching). Empty when everything above is green.
  Future<List<String>> _doctorRecommendations(
    EnvironmentReport env,
    DeveloperDoctorReport report,
  ) async {
    final recommendations = <String>[];
    if (env.blockers.isNotEmpty) {
      recommendations.add(
        'Run `topiaforge setup` to apply safe fixes, then install anything '
        'still marked [ X ] above.',
      );
    }
    if (!report.hasProject) {
      // Not inside a developer project — normal for the repo root, bare mod
      // dirs, or a player machine; orientation rather than a finding.
      recommendations.add(
        'Project checks were skipped: run inside a scaffolded mod project '
        'or pass --project (create one with `topiaforge new mod <id>`).',
      );
    } else if (report.issues.any((issue) => issue.isBlocking)) {
      recommendations.add(
        'Fix the project errors above, then re-check with '
        '`topiaforge check project` (manifest reference: docs/Modding.md).',
      );
    } else if (report.issues.any(
      (issue) => issue.severity == IssueSeverity.warning,
    )) {
      recommendations.add(
        'Review the project warnings above — publishing to the official '
        'registry requires zero findings (docs/PublishingYourMod.md).',
      );
    }
    try {
      final launcher = LocalLauncherRepository(
        repositoryRoot: _findRepoRoot() ?? Directory.current.path,
      );
      if (await launcher.detectKnownInstall() == null) {
        recommendations.add(
          'Robotopia install not detected — set ROBOTOPIA_GAME_DIR or pass '
          '--game-dir (see docs/Troubleshooting.md).',
        );
      }
    } on Object {
      // Detection is best-effort; never let it fail the doctor run.
    }
    return recommendations;
  }

  Future<int> _compat(List<String> args) async {
    if (args.firstOrNull == 'bump') {
      return _compatBump(args.skip(1).toList());
    }
    final result = await _runGameCompat(
      managed: _option(args, '--managed'),
      json: args.contains('--json'),
    );
    if (result == null) {
      stderr.writeln('Could not run the GameCompat extractor.');
      stderr.writeln(
        '  Repair the TopiaForge release, or build src/TopiaForge.GameCompat.Extractor from source.',
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

  /// Retargets every derivable reference to the pinned Robotopia build.
  ///
  /// Bindings and the compatibility baseline are deliberately untouched: those
  /// are a reviewed act, so this prints the ritual instead of guessing at it.
  Future<int> _compatBump(List<String> args) async {
    int requireInt(String flag) {
      final raw = _option(args, flag);
      final value = raw == null ? null : int.tryParse(raw);
      if (value == null) {
        throw UsageError('$flag requires a positive integer.');
      }
      return value;
    }

    String requireValue(String flag) {
      final raw = _option(args, flag);
      if (raw == null || raw.isEmpty) {
        throw UsageError('$flag is required.');
      }
      return raw;
    }

    final dryRun = args.contains('--dry-run');
    final GameBuildBumpResult result;
    try {
      result = bumpRobotopiaGameBuild(
        repositoryRoot: _releaseRepositoryRoot(),
        toBuildId: requireInt('--build'),
        windowsArchiveSha256: requireValue('--windows-sha256'),
        macArchiveSha256: requireValue('--mac-sha256'),
        filesManifestSha256: requireValue('--files-manifest-sha256'),
        filesManifestFileCount: requireInt('--file-count'),
        gameExecutableSha256: requireValue('--game-exe-sha256'),
        dryRun: dryRun,
      );
    } on ArgumentError catch (error) {
      throw UsageError('${error.name}: ${error.message}');
    } on StateError catch (error) {
      stderr.writeln(error.message);
      return 1;
    }

    final prefix = dryRun ? 'Would update' : 'Updated';
    stdout.writeln(
      '$prefix ${result.edits.length} file(s), '
      '${result.totalReplacements} reference(s): '
      'build ${result.fromBuildId} -> ${result.toBuildId}.',
    );
    for (final edit in result.edits) {
      stdout.writeln('  ${edit.path} (${edit.replacements})');
    }
    if (!result.isComplete) {
      stderr.writeln(
        'Incomplete bump: these files still mention build '
        '${result.fromBuildId}:',
      );
      for (final path in result.residual) {
        stderr.writeln('  $path');
      }
      return 1;
    }
    if (dryRun) {
      return 0;
    }
    stdout.writeln('');
    stdout.writeln('Bindings and the compatibility baseline are NOT bumped.');
    stdout.writeln('Next, against an installed build ${result.toBuildId}:');
    stdout.writeln('  1. gamecompat verify   (resolve every declared binding)');
    stdout.writeln('  2. adapt any binding the report flags');
    stdout.writeln(
      '  3. gamecompat baseline (review the printed surface diff)',
    );
    stdout.writeln('  4. re-run the offline test gate');
    stdout.writeln('');
    stdout.writeln(
      'Review by hand: the SDK-only ceiling in mod manifests and '
      'FirstPartyManifestTests is a judgement call, not a derivation.',
    );
    return 0;
  }

  Future<ProcessResult?> _runGameCompat({
    String? managed,
    bool json = false,
  }) async {
    final verifyArgs = <String>[
      'verify',
      if (managed != null) ...['--managed', managed],
      '--format',
      json ? 'json' : 'text',
    ];

    final packaged = const GameCompatExecutableLocator().findPackaged(
      resolvedExecutable: Platform.resolvedExecutable,
    );
    if (packaged != null) {
      return _runGameCompatExecutable(
        packaged,
        verifyArgs,
        workingDirectory: File(packaged).parent.path,
      );
    }

    final root = _findRepoRoot();
    if (root == null) {
      return null;
    }

    for (final config in ['Release', 'Debug']) {
      final binDir =
          '$root/src/TopiaForge.GameCompat.Extractor/bin/$config/net10.0';
      for (final name in [
        'TopiaForge.GameCompat.Extractor.exe',
        'TopiaForge.GameCompat.Extractor',
      ]) {
        final exe = '$binDir/$name';
        if (File(exe).existsSync()) {
          return _runGameCompatExecutable(
            exe,
            verifyArgs,
            workingDirectory: root,
          );
        }
      }
    }

    late final DotnetSdkSelection dotnet;
    try {
      dotnet = await resolveRepositoryDotnetSdk(Directory(root));
    } on Object catch (error) {
      stderr.writeln(
        'GameCompat could not select the repository .NET SDK: '
        '${_environmentErrorMessage(error)}',
      );
      return null;
    }
    try {
      final run = await runBoundedProcess(
        dotnet.executable,
        [
          'run',
          '--project',
          '$root/src/TopiaForge.GameCompat.Extractor',
          '-c',
          'Release',
          '--',
          ...verifyArgs,
        ],
        workingDirectory: root,
        timeout: _environmentDotnetTimeout,
        maxStdoutBytes: _environmentDotnetOutputLimit ~/ 2,
        maxStderrBytes: _environmentDotnetOutputLimit ~/ 2,
      );
      return ProcessResult(0, run.exitCode, run.stdout, run.stderr);
    } on BoundedProcessException catch (error) {
      stderr.writeln(
        _environmentBoundedProcessFailure(
          'GameCompat dotnet run',
          dotnet.executable,
          error,
        ),
      );
      return null;
    } on Object catch (error) {
      stderr.writeln(
        'GameCompat could not start the verified .NET host '
        '${dotnet.executable}: '
        '${_environmentErrorMessage(error)}',
      );
      return null;
    }
  }

  Future<ProcessResult?> _runGameCompatExecutable(
    String executable,
    List<String> arguments, {
    required String workingDirectory,
  }) async {
    try {
      final run = await runBoundedProcess(
        executable,
        arguments,
        workingDirectory: workingDirectory,
        timeout: _gameCompatTimeout,
        maxStdoutBytes: _gameCompatOutputLimit ~/ 2,
        maxStderrBytes: _gameCompatOutputLimit ~/ 2,
      );
      return ProcessResult(0, run.exitCode, run.stdout, run.stderr);
    } on BoundedProcessException catch (error) {
      stderr.writeln(
        _environmentBoundedProcessFailure(
          'GameCompat extractor',
          executable,
          error,
          timeout: _gameCompatTimeout,
          combinedOutputLimit: _gameCompatOutputLimit,
        ),
      );
      return null;
    } on Object catch (error) {
      stderr.writeln(
        'GameCompat could not start $executable: '
        '${_environmentErrorMessage(error)}',
      );
      return null;
    }
  }

  String? _findRepoRoot() {
    var dir = Directory.current;
    while (true) {
      if (File('${dir.path}/TopiaForge.slnx').existsSync()) {
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
    stdout.writeln('TopiaForge developer setup');
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
        'Ready to build mods. Next: topiaforge new mod <id> --name "My Mod".',
      );
    } else {
      stdout.writeln(
        'Install the missing developer tools listed above, then re-run `topiaforge setup`.',
      );
    }
    stdout.writeln(
      'To only consume mods you need none of this — use the launcher, or `topiaforge install <package>` then `topiaforge launch`.',
    );
    return result.environment.developerReady ? 0 : 1;
  }

  void _printEnvironment(EnvironmentReport env) {
    stdout.writeln(
      'Consuming mods needs no developer tools — use the launcher, or `topiaforge install <package>` then `topiaforge launch`.',
    );
    stdout.writeln('');
    stdout.writeln('Build mods (.NET, required to develop):');
    _printChecks(env.ofPurpose(ToolPurpose.develop));
    stdout.writeln('Custom worlds (optional):');
    _printChecks(env.ofPurpose(ToolPurpose.customWorldUnity).toList());
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
        'The TopiaForge repository root was not found from '
        '${Directory.current.path}.',
      );
    }
    final configuration = _option(args, '--configuration') ?? 'Release';

    if (!args.contains('--skip-build')) {
      stdout.writeln('Building TopiaForge.slnx ($configuration)...');
      final dotnet = await resolveRepositoryDotnetSdk(Directory(repoRoot));
      late final BoundedProcessResult build;
      try {
        build = await runBoundedProcess(
          dotnet.executable,
          ['build', p.join(repoRoot, 'TopiaForge.slnx'), '-c', configuration],
          workingDirectory: repoRoot,
          timeout: _environmentDotnetTimeout,
          maxStdoutBytes: _environmentDotnetOutputLimit ~/ 2,
          maxStderrBytes: _environmentDotnetOutputLimit ~/ 2,
        );
      } on BoundedProcessException catch (error) {
        stderr.writeln(
          _environmentBoundedProcessFailure(
            'TopiaForge solution build',
            dotnet.executable,
            error,
          ),
        );
        return 1;
      }
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
      'TopiaForge',
      'package-inbox',
    );
    Directory(inbox).createSync(recursive: true);
    Directory(
      p.join(install.path, 'BepInEx', 'TopiaForge', 'logs'),
    ).createSync(recursive: true);

    final staged = await _packAllMods(
      outputDir: inbox,
      configuration: configuration,
      // Dev installs stage everything, including DevTool-category mods.
      includeDevMods: !args.contains('--no-dev-mods'),
    );
    stdout.writeln('');
    stdout.writeln('Installed the TopiaForge runtime into ${install.path}');
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
    stderr.writeln('Run `topiaforge setup` for setup help.');
    return false;
  }
}

String _environmentErrorMessage(Object error) =>
    error is StateError ? error.message : error.toString();

String _environmentBoundedProcessFailure(
  String operation,
  String executable,
  BoundedProcessException error, {
  Duration timeout = _environmentDotnetTimeout,
  int combinedOutputLimit = _environmentDotnetOutputLimit,
}) => switch (error.failure) {
  BoundedProcessFailure.timeout =>
    '$operation timed out after ${timeout.inMinutes} minutes '
        'and was terminated: $executable.',
  BoundedProcessFailure.stdoutLimitExceeded ||
  BoundedProcessFailure.stderrLimitExceeded =>
    '$operation exceeded the '
        '${combinedOutputLimit ~/ (1024 * 1024)} MiB combined '
        'output limit and was terminated: $executable.',
  BoundedProcessFailure.outputReadFailed =>
    '$operation output could not be read; the process was terminated: '
        '$executable.',
  BoundedProcessFailure.terminationFailed =>
    '$operation exceeded a safety bound and could not be terminated: '
        '$executable.',
};

const _environmentDotnetTimeout = Duration(minutes: 10);
const _environmentDotnetOutputLimit = 16 * 1024 * 1024;
const _gameCompatTimeout = Duration(minutes: 2);
const _gameCompatOutputLimit = 4 * 1024 * 1024;
