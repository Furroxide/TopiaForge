part of 'topiaforge.dart';

/// Runs the independently compiled C# release-scaffold verifier shipped beside
/// the CLI. This makes clean-machine portability and receipt checks available
/// to modders without duplicating the scaffold generator's Dart implementation.
extension _TopiaForgeReleaseScaffoldValidation on _TopiaForgeCli {
  static const _usage =
      'Usage: topiaforge check scaffold <project> [--forbid path]... '
      '[--package archive --installed-packages directory]';

  Future<int> _checkReleaseScaffold(List<String> args) async {
    String? project;
    String? package;
    String? installedPackages;
    final forbidden = <String>[];
    for (var index = 0; index < args.length; index++) {
      final argument = args[index];
      if (const {
        '--forbid',
        '--package',
        '--installed-packages',
      }.contains(argument)) {
        if (++index >= args.length || args[index].trim().isEmpty) {
          throw UsageError('$_usage\n$argument requires a path.');
        }
        switch (argument) {
          case '--forbid':
            forbidden.add(args[index]);
          case '--package':
            package = args[index];
          case '--installed-packages':
            installedPackages = args[index];
        }
        continue;
      }
      if (argument.startsWith('--') || project != null) {
        throw UsageError(_usage);
      }
      project = argument;
    }

    if (project == null || (package == null) != (installedPackages == null)) {
      throw UsageError(_usage);
    }

    try {
      final validator = _managedPackageValidator();
      final hostRoot = _findPinnedDotnetRoot(validator);
      final dotnet = await resolveRepositoryDotnetSdk(hostRoot);
      final validatorArgs = <String>[
        validator.path,
        'release-scaffold',
        project,
        for (final path in forbidden) ...['--forbid', path],
        if (package != null) ...['--package', package],
        if (installedPackages != null) ...[
          '--installed-packages',
          installedPackages,
        ],
      ];
      final result = await runBoundedProcess(
        dotnet.executable,
        validatorArgs,
        workingDirectory: hostRoot.path,
        runInShell: false,
        timeout: const Duration(minutes: 2),
        maxStdoutBytes: 1024 * 1024,
        maxStderrBytes: 4 * 1024 * 1024,
      );
      if (result.stdout.trim().isNotEmpty) stdout.write(result.stdout);
      if (result.stderr.trim().isNotEmpty) stderr.write(result.stderr);
      return result.exitCode;
    } on Object catch (error) {
      final cause = error is StateError ? error.message : error.toString();
      stderr.writeln(
        'TFSCF171: release scaffold validation could not complete.\n'
        'Cause: $cause\n'
        'Remediation: repair or re-extract the TopiaForge release, then retry.\n'
        'Docs: https://docs.topiaforge.dev/diagnostics/TFSCF171',
      );
      return 1;
    }
  }
}
