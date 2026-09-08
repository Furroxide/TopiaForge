part of 'topiaforge.dart';

extension _TopiaForgeReadinessCommands on _TopiaForgeCli {
  Future<int> _releaseValidatePrerequisites(List<String> args) async {
    final options = _readinessOptions(args, candidate: false);
    final assessment = ReleasePrerequisites(
      await ReleaseReadinessDecision.loadAtGitSha(
        repositoryRoot: _releaseRepositoryRoot(),
        targetSha: options['--target-sha']!,
        expectedReleaseVersion: options['--version']!,
      ),
    );
    for (final gate in assessment.decision.gates) {
      if (gate.isSatisfied) continue;
      final blocks = gate.blocksRelease && gate.id != 'P0-GAME-01';
      stderr.writeln(
        '${blocks ? 'error' : 'warning'}: Release prerequisite ${gate.id} '
        'is ${gate.status} (${gate.enforcement}).',
      );
    }
    stdout.writeln(
      const JsonEncoder.withIndent('  ').convert(assessment.toPublicSummary()),
    );
    return assessment.isEligible ? 0 : 1;
  }

  Future<int> _releaseValidateReadiness(List<String> args) async {
    final options = _readinessOptions(args, candidate: true);
    final qualification = await ReleaseCandidateQualification.loadAtGitSha(
      repositoryRoot: _releaseRepositoryRoot(),
      targetSha: options['--target-sha']!,
      expectedReleaseVersion: options['--version']!,
      assetsDirectory: options['--assets']!,
    );
    for (final gate in qualification.gates) {
      if (!gate.isSatisfied) {
        stderr.writeln('warning: Release gate ${gate.id} is ${gate.status}.');
      }
    }
    stdout.writeln(
      const JsonEncoder.withIndent(
        '  ',
      ).convert(qualification.toPublicSummary()),
    );
    return qualification.isReady ? 0 : 1;
  }

  Map<String, String> _readinessOptions(
    List<String> args, {
    required bool candidate,
  }) {
    final required = {'--version', '--target-sha', if (candidate) '--assets'};
    final values = <String, String>{};
    for (var index = 0; index < args.length; index += 2) {
      final name = args[index];
      if (!required.contains(name) ||
          values.containsKey(name) ||
          index + 1 >= args.length ||
          args[index + 1].startsWith('--') ||
          args[index + 1].trim().isEmpty) {
        throw UsageError(
          'Unknown, duplicate, or missing readiness option: $name.',
        );
      }
      values[name] = args[index + 1];
    }
    final missing = required.difference(values.keys.toSet());
    if (missing.isNotEmpty) {
      throw UsageError('Required readiness options: ${missing.join(', ')}.');
    }
    return values;
  }
}
