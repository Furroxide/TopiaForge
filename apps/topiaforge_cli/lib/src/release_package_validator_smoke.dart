part of 'release_package_validator.dart';

extension _ReleasePackageValidatorSmoke on ReleasePackageValidator {
  Future<void> _assertCliRuns(String cliPath) async {
    await _assertExecutable(cliPath);
    if (!runCliSmoke) {
      return;
    }
    final result = await _runCliSmoke(cliPath, const ['--help']);
    if (result.exitCode != 0) {
      throw StateError('CLI help failed with exit ${result.exitCode}.');
    }
    final output = '${result.stdout}\n${result.stderr}';
    if (!output.contains('TopiaForge CLI')) {
      throw StateError('CLI help output did not contain the expected banner.');
    }
    if (requireRuntimePayload) {
      await _assertPackagedGameCompatRuns(cliPath);
    }
  }

  Future<void> _assertPackagedGameCompatRuns(String cliPath) async {
    final result = await _runCliSmoke(cliPath, const ['compat', '--json']);
    if (result.exitCode != 0 && result.exitCode != 1) {
      throw StateError(
        'Packaged GameCompat check failed with exit ${result.exitCode}.',
      );
    }

    Object? decoded;
    try {
      decoded = jsonDecode(result.stdout.toString());
    } on FormatException {
      // Report a stable packaging failure rather than leaking an arbitrarily
      // large or machine-specific child-process response.
    }
    final status = decoded is Map<String, dynamic> ? decoded['status'] : null;
    if (status != 'skipped' && status != 'ok' && status != 'broken') {
      throw StateError(
        'Packaged CLI did not return a GameCompat JSON report. The sibling '
        'extractor may be missing or unreachable.',
      );
    }
  }

  Future<ProcessResult> _runCliSmoke(
    String cliPath,
    List<String> arguments,
  ) async {
    try {
      return await processRunner.runBoundedResult(
        cliPath,
        arguments,
        workingDirectory: File(cliPath).parent.path,
        timeout: _releaseCliSmokeTimeout,
        maxStdoutBytes: _releaseCliSmokeOutputLimit ~/ 2,
        maxStderrBytes: _releaseCliSmokeOutputLimit ~/ 2,
      );
    } on BoundedProcessException catch (error) {
      throw StateError(
        'Embedded CLI smoke test exceeded its execution bounds: '
        '${error.failure.name}.',
      );
    }
  }
}

const _releaseCliSmokeTimeout = Duration(minutes: 2);
const _releaseCliSmokeOutputLimit = 4 * 1024 * 1024;
