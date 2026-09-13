import 'dart:convert';
import 'dart:io';

import 'package:test/test.dart';
import 'package:topiaforge/src/sandbox_acceptance/sandbox_specification.dart';

import 'sandbox_acceptance_fixture.dart';

void main() {
  test(
    'developer verifier exit codes distinguish offline success, gaps and refusal',
    () async {
      final owned = Directory.systemTemp.createTempSync(
        'TopiaForgeSandboxVerifier-',
      );
      addTearDown(() => owned.deleteSync(recursive: true));
      final spec = SandboxSpecification.parse(sandboxSpecBytes);
      final input = File('${owned.path}/observations.json');
      final report = sandboxObservations(spec);
      Future<ProcessResult> run({bool includeInputs = true}) => Process.run(
        Platform.resolvedExecutable,
        [
          'run',
          'tool/verify_sandbox_observations.dart',
          if (includeInputs) ...[
            '$sandboxRepositoryRoot/tests/sandbox-workbench-acceptance-v1.json',
            input.path,
          ],
        ],
        workingDirectory: '$sandboxRepositoryRoot/apps/topiaforge_cli',
      );

      input.writeAsBytesSync(sandboxJsonBytes(report));
      final passed = await run();
      expect(passed.exitCode, 0, reason: '${passed.stderr}');
      final output =
          jsonDecode(passed.stdout as String) as Map<String, Object?>;
      expect(output['scope'], 'supplementary-offline-contracts');
      expect(output['qualifiesRelease'], false);
      expect(output['remainingRequirements'], isNotEmpty);

      report['observations'] = <Object?>[];
      input.writeAsBytesSync(sandboxJsonBytes(report));
      final missing = await run();
      expect(missing.exitCode, 1, reason: '${missing.stderr}');
      expect(
        (jsonDecode(missing.stdout as String) as Map)['allOfflineChecksPassed'],
        false,
      );

      report['passed'] = true;
      input.writeAsBytesSync(sandboxJsonBytes(report));
      final refused = await run();
      expect(refused.exitCode, 2);
      expect(refused.stdout, isEmpty);
      expect(refused.stderr, contains('verification refused'));

      input.writeAsBytesSync(List.filled(512 * 1024, 32));
      final oversized = await run();
      expect(oversized.exitCode, 2);
      expect(oversized.stderr, contains('oversized'));
      input.deleteSync();
      expect((await run()).exitCode, 2);
      final usage = await run(includeInputs: false);
      expect(usage.exitCode, 64);
      expect(usage.stdout, isEmpty);
      expect(usage.stderr, contains('Usage:'));
    },
  );
}
