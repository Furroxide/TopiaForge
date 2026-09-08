import 'dart:io';

import 'package:path/path.dart' as p;
import 'package:test/test.dart';

void main() {
  final root = Directory.current.parent.parent.path;
  final workflow = File(
    p.join(root, '.github/workflows/release.yml'),
  ).readAsStringSync().replaceAll('\r\n', '\n');
  String job(String name, String? next) {
    final start = workflow.indexOf('  $name:');
    final end = next == null
        ? workflow.length
        : workflow.indexOf('  $next:', start);
    return workflow.substring(start, end);
  }

  test('all publication readiness checks bind downloaded candidate bytes', () {
    final calls = RegExp(
      r'release validate-readiness[\s\S]*?(?=\n\s*\n|$)',
    ).allMatches(workflow).map((match) => match.group(0)!).toList();
    expect(calls.length, greaterThanOrEqualTo(3));
    for (final call in calls) {
      expect(call, contains('--assets '), reason: call);
    }
    for (final phase in [
      job('verify-candidate', 'verify-platform-bundles'),
      job('finalize', null),
    ]) {
      expect(
        phase.indexOf('fetch-release-assets.sh'),
        lessThan(phase.indexOf('release validate-readiness')),
        reason: 'Qualification must use this phase’s freshly fetched bytes.',
      );
    }
  });
  test(
    'approval pins detached decision and acceptance across fresh downloads',
    () {
      final initial = job('verify-candidate', 'verify-platform-bundles');
      final approved = job('finalize', null);
      expect(initial, contains('decision_sha256:'));
      expect(initial, contains('acceptance_sha256:'));
      for (final digest in ['decision_sha256', 'acceptance_sha256']) {
        expect(approved, contains('needs.verify-candidate.outputs.$digest'));
      }
      expect(
        approved.indexOf(r'asset_policy=$(bash tools/release-asset-policy.sh'),
        lessThan(approved.indexOf('release build-metadata')),
        reason: 'Metadata is downstream of both detached records.',
      );
    },
  );
}
