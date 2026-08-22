part of 'topiaforge.dart';

/// `topiaforge compat ...` — the GameCompat extractor and the pinned-build retarget.
///
/// Split out of `topiaforge_environment_commands.dart`, which `compat bump` pushed past
/// the 500-line cap in AGENTS.md.
extension _TopiaForgeCompatCommands on _TopiaForgeCli {
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
}

const _gameCompatTimeout = Duration(minutes: 2);
const _gameCompatOutputLimit = 4 * 1024 * 1024;
