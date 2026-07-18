part of 'topiaforge.dart';

/// The V1 code-mod inner loop. Every stage reports a stable diagnostic code so
/// editor integrations and beginners can act on a failure without parsing tool output.
extension _TopiaForgeDevCommands on _TopiaForgeCli {
  static const _devUsage =
      'Usage: topiaforge dev [--project path] [--configuration name] '
      '[--game-dir path] [--no-launch] [--no-tail]';

  Future<int> _dev(List<String> args) async {
    _validateDevArguments(args);
    if (args.contains('--help')) {
      stdout.writeln(_devUsage);
      stdout.writeln(
        'Interactive terminals launch Robotopia and tail attributed logs by '
        'default. Redirected/CI runs stop after install.',
      );
      return 0;
    }

    final requestedProject = p.normalize(
      p.absolute(_option(args, '--project') ?? Directory.current.path),
    );
    final configuration = _option(args, '--configuration') ?? 'Release';
    if (!RegExp(r'^[A-Za-z0-9_.-]{1,64}$').hasMatch(configuration)) {
      throw UsageError('Invalid --configuration value.\n$_devUsage');
    }
    if (args.contains('--launch') && args.contains('--no-launch')) {
      throw UsageError('--launch and --no-launch cannot be combined.');
    }
    if (args.contains('--tail') && args.contains('--no-tail')) {
      throw UsageError('--tail and --no-tail cannot be combined.');
    }

    final interactive = stdin.hasTerminal && stdout.hasTerminal;
    final launch =
        args.contains('--launch') ||
        (interactive && !args.contains('--no-launch'));
    final tail =
        args.contains('--tail') ||
        (interactive && launch && !args.contains('--no-tail'));
    final gameDir = _option(args, '--game-dir');

    try {
      final workspace = await _runDevStage(
        const _DevStage(
          code: 'TFDEV100',
          label: 'restore',
          action: 'Restore the exact SDK and dependency locks.',
          remediation:
              'Run `topiaforge restore --project <path>` and resolve every reported lock, feed, or SDK issue.',
        ),
        () async {
          final restored = await developerRepository.resolveDeveloperProject(
            requestedProject,
            restore: true,
          );
          if (!restored.hasProject) {
            throw StateError(
              'No TopiaForge project was found from $requestedProject.',
            );
          }
          if (restored.hasBlockingIssues) {
            throw StateError(
              restored.issues
                  .where((issue) => issue.isBlocking)
                  .map((issue) => issue.message)
                  .join(' '),
            );
          }
          return restored;
        },
      );
      final projectRoot = workspace.projectRoot;
      final dotnet = await _runDevStage(
        const _DevStage(
          code: 'TFDEV105',
          label: 'toolchain',
          action: 'Activate the project-pinned .NET SDK.',
          remediation:
              'Install the exact SDK pinned by global.json, then rerun `topiaforge restore`.',
        ),
        () => resolveRepositoryDotnetSdk(Directory(projectRoot)),
      );

      await _runDevStage(
        const _DevStage(
          code: 'TFDEV110',
          label: 'build',
          action: 'Build the mod against the locked SDK.',
          remediation:
              'Fix the compiler diagnostics, then run `dotnet build --no-restore` from the mod project.',
        ),
        () async {
          final manifest = await developerRepository.readModManifest(
            projectRoot,
          );
          final entryProject = _findEntryProject(
            Directory(projectRoot),
            manifest,
          );
          await _runDevDotnet(dotnet.executable, [
            'build',
            entryProject.path,
            '-c',
            configuration,
            '--no-restore',
            '--nologo',
          ], projectRoot);
        },
      );

      await _runDevStage(
        const _DevStage(
          code: 'TFDEV120',
          label: 'test',
          action: 'Run every generated or authored test project.',
          remediation:
              'Fix the failing test or test-host error, then run `dotnet test --no-restore`.',
        ),
        () async {
          final tests = _findDevTestProjects(projectRoot);
          if (tests.isEmpty) {
            stdout.writeln(
              '  No *.Tests.csproj project found; test stage skipped.',
            );
            return;
          }
          for (final testProject in tests) {
            await _runDevDotnet(dotnet.executable, [
              'test',
              testProject.path,
              '-c',
              configuration,
              '--no-restore',
              '--nologo',
            ], projectRoot);
          }
        },
      );

      final packagePath = await _runDevStage(
        const _DevStage(
          code: 'TFDEV130',
          label: 'pack',
          action: 'Create the installable .topiaforgemod archive.',
          remediation:
              'Check the manifest, entry assembly, package files, and Release build output; then run `topiaforge pack`.',
        ),
        () {
          final output = p.join(
            projectRoot,
            'bin',
            'TopiaForgeDev',
            configuration,
          );
          final hasProjectFile = File(
            p.join(projectRoot, 'topiaforge.project.json'),
          ).existsSync();
          return hasProjectFile
              ? developerRepository.packProject(
                  projectRoot,
                  outputDir: output,
                  configuration: configuration,
                )
              : developerRepository.packModDirectory(
                  projectRoot,
                  outputDir: output,
                  configuration: configuration,
                );
        },
      );

      await _runDevStage(
        const _DevStage(
          code: 'TFDEV140',
          label: 'validate',
          action: 'Validate the exact archive that will be installed.',
          remediation:
              'Run `topiaforge check package <archive>` and correct every blocking manifest or archive issue.',
        ),
        () async {
          await developerRepository.checkPackage(packagePath);
          final assemblyErrors = await _managedPackageValidationErrors(
            packagePath,
            dotnetExecutable: dotnet.executable,
            dotnetRoot: projectRoot,
          );
          if (assemblyErrors.isNotEmpty) {
            throw StateError(assemblyErrors.join(' '));
          }
        },
      );

      final launcher = LocalLauncherRepository(
        knownGamePath: gameDir,
        repositoryRoot: _findRepoRoot(),
        workingDirectory: projectRoot,
      );
      try {
        final install = await _runDevStage(
          const _DevStage(
            code: 'TFDEV150',
            label: 'install',
            action: 'Install the validated package into Robotopia.',
            remediation:
                'Pass `--game-dir <Robotopia folder>` or set ROBOTOPIA_GAME_DIR, then run `topiaforge doctor`.',
          ),
          () async {
            final detected = await launcher.detectKnownInstall();
            if (detected == null) throw StateError(_noInstallRemedy);
            await launcher.installPackage(packagePath, detected);
            return detected;
          },
        );

        if (launch) {
          await _runDevStage(
            const _DevStage(
              code: 'TFDEV160',
              label: 'launch',
              action: 'Launch Robotopia with the selected profile.',
              remediation:
                  'Run `topiaforge doctor`, repair the runtime in the launcher, and retry with `--no-tail` while diagnosing.',
            ),
            () async {
              final snapshot = await launcher.loadSnapshot();
              if (snapshot.profiles.isEmpty) {
                throw StateError('No launcher profile is available.');
              }
              final profile = snapshot.profiles.firstWhere(
                (item) => item.id == snapshot.selectedProfileId,
                orElse: () => snapshot.profiles.first,
              );
              final result = await launcher.launch(install, profile);
              if (!result.started) throw StateError(result.message);
              stdout.writeln('  ${result.message}');
            },
          );
        } else {
          stdout.writeln('[launch] skipped (non-interactive or --no-launch).');
        }

        if (tail) {
          await _runDevStage(
            const _DevStage(
              code: 'TFDEV170',
              label: 'tail',
              action: 'Tail launcher and manager logs with source labels.',
              remediation:
                  'Inspect BepInEx/TopiaForge/logs and the launcher diagnostics bundle, or rerun with `--no-tail`.',
            ),
            () => _tailDevLogs(launcher, install),
          );
        } else {
          stdout.writeln('[tail] skipped (non-interactive or --no-tail).');
        }
      } finally {
        await launcher.dispose();
      }

      stdout.writeln('TopiaForge dev loop completed: $packagePath');
      return 0;
    } on _DevStageFailure catch (failure) {
      stderr.writeln('${failure.stage.code}: ${failure.stage.label} failed.');
      stderr.writeln('Cause: ${_devCause(failure.cause)}');
      stderr.writeln('Remediation: ${failure.stage.remediation}');
      stderr.writeln(
        'Docs: https://docs.topiaforge.dev/diagnostics/${failure.stage.code}',
      );
      return 1;
    }
  }

  Future<T> _runDevStage<T>(
    _DevStage stage,
    Future<T> Function() action,
  ) async {
    stdout.writeln('[${stage.label}] ${stage.action}');
    try {
      final value = await action();
      stdout.writeln('[${stage.label}] complete.');
      return value;
    } on Object catch (error) {
      throw _DevStageFailure(stage, error);
    }
  }

  Future<void> _runDevDotnet(
    String executable,
    List<String> arguments,
    String workingDirectory,
  ) async {
    final result = await runBoundedProcess(
      executable,
      arguments,
      workingDirectory: workingDirectory,
      timeout: const Duration(minutes: 5),
      maxStdoutBytes: 8 * 1024 * 1024,
      maxStderrBytes: 8 * 1024 * 1024,
    );
    if (result.exitCode != 0) {
      final output = [
        if (result.stderr.trim().isNotEmpty) result.stderr.trim(),
        if (result.stdout.trim().isNotEmpty) result.stdout.trim(),
      ].join('\n');
      throw StateError(
        'dotnet ${arguments.first} exited ${result.exitCode}. '
        '${_boundedDevText(output)}',
      );
    }
  }

  List<File> _findDevTestProjects(String projectRoot) {
    final files = Directory(projectRoot)
        .listSync(recursive: true, followLinks: false)
        .whereType<File>()
        .where((file) {
          final parts = p.split(file.path);
          return p.basenameWithoutExtension(file.path).endsWith('.Tests') &&
              p.extension(file.path).toLowerCase() == '.csproj' &&
              !parts.contains('bin') &&
              !parts.contains('obj');
        })
        .toList();
    files.sort((left, right) => left.path.compareTo(right.path));
    return files;
  }

  Future<void> _tailDevLogs(
    LocalLauncherRepository launcher,
    GameInstall install,
  ) async {
    stdout.writeln('Press Ctrl+C to stop log tailing.');
    final stopped = Completer<void>();
    StreamSubscription<ProcessSignal>? signal;
    try {
      signal = ProcessSignal.sigint.watch().listen((_) {
        if (!stopped.isCompleted) stopped.complete();
      });
    } on Object {
      // Signal streams are unavailable on a few embedded hosts. Tailing still
      // works and the host can terminate the command normally.
    }

    var previous = <String>[];
    try {
      while (!stopped.isCompleted) {
        final currentText = await launcher.readRecentLog(
          install,
          maxLines: 400,
        );
        final current = currentText.isEmpty
            ? <String>[]
            : currentText.split('\n');
        final overlap = _devLineOverlap(previous, current);
        for (final line in current.skip(overlap)) {
          stdout.writeln(line);
        }
        previous = current;
        await Future.any<void>([
          Future<void>.delayed(const Duration(milliseconds: 500)),
          stopped.future,
        ]);
      }
    } finally {
      await signal?.cancel();
    }
  }

  void _validateDevArguments(List<String> args) {
    const valueOptions = {'--project', '--configuration', '--game-dir'};
    const switches = {
      '--help',
      '--no-launch',
      '--no-tail',
      // Explicit counterparts are useful for redirected IDE terminals while
      // the safe non-interactive default remains no launch/no tail.
      '--launch',
      '--tail',
    };
    for (var index = 0; index < args.length; index++) {
      final arg = args[index];
      if (switches.contains(arg)) continue;
      if (valueOptions.contains(arg)) {
        if (index + 1 >= args.length || args[index + 1].startsWith('--')) {
          throw UsageError('$arg requires a value.\n$_devUsage');
        }
        index++;
        continue;
      }
      throw UsageError('Unknown dev option: $arg\n$_devUsage');
    }
  }
}

final class _DevStage {
  const _DevStage({
    required this.code,
    required this.label,
    required this.action,
    required this.remediation,
  });

  final String code;
  final String label;
  final String action;
  final String remediation;
}

final class _DevStageFailure implements Exception {
  const _DevStageFailure(this.stage, this.cause);

  final _DevStage stage;
  final Object cause;
}

String _devCause(Object error) {
  final text = error is StateError ? error.message : error.toString();
  return _boundedDevText(text.replaceAll(RegExp(r'\s+'), ' ').trim());
}

String _boundedDevText(String text) {
  const limit = 4000;
  if (text.length <= limit) return text;
  return '…${text.substring(text.length - limit)}';
}

int _devLineOverlap(List<String> previous, List<String> current) {
  final maximum = previous.length < current.length
      ? previous.length
      : current.length;
  for (var count = maximum; count > 0; count--) {
    var matches = true;
    for (var index = 0; index < count; index++) {
      if (previous[previous.length - count + index] != current[index]) {
        matches = false;
        break;
      }
    }
    if (matches) return count;
  }
  return 0;
}
