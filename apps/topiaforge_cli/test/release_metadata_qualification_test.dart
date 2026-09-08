import 'dart:convert';
import 'dart:io';

import 'package:path/path.dart' as p;
import 'package:test/test.dart';
import 'package:topiaforge/src/release_metadata_readiness.dart';

import 'release_candidate_fixture.dart';

void main() {
  test(
    'publication metadata binds the validated detached summary exactly',
    () async {
      final fixture = await CandidateFixture.create();
      addTearDown(fixture.dispose);
      final qualified = await fixture.qualify();
      final readiness = await ReleaseMetadataReadiness.load(
        repositoryRoot: fixture.root.path,
        version: CandidateFixture.version,
        targetSha: fixture.sha,
        assetsDirectory: fixture.assets.path,
        allowUnresolved: false,
      );
      expect(readiness.toBomJson(), qualified.toBomJson());
      expect(readiness.status, 'ready');
      expect(readiness.blockingReasons, isEmpty);
      expect(
        releaseMetadataBlockingReasons(
          allowUnresolved: true,
          licenseApproved: true,
          licenseStatus: 'approved',
          licenseExpression: 'MIT',
          catalogStatus: 'ready',
          readiness: readiness,
        ),
        ['Unresolved-policy mode is non-distributable.'],
      );
      fixture
          .asset('release-candidate-acceptance-v1.json')
          .writeAsStringSync('\n', mode: FileMode.append);
      await expectLater(
        ReleaseMetadataReadiness.load(
          repositoryRoot: fixture.root.path,
          version: CandidateFixture.version,
          targetSha: fixture.sha,
          assetsDirectory: fixture.assets.path,
          allowUnresolved: false,
        ),
        throwsStateError,
      );
      final unresolved = await ReleaseMetadataReadiness.load(
        repositoryRoot: fixture.root.path,
        version: CandidateFixture.version,
        targetSha: fixture.sha,
        assetsDirectory: fixture.assets.path,
        allowUnresolved: true,
      );
      expect(unresolved.status, 'unavailable');
      expect(unresolved.summary, isNull);
      expect(unresolved.blockingReasons, isNotEmpty);
    },
  );

  test(
    'a ready tracked decision cannot bypass detached qualification',
    () async {
      final root = Directory.current.parent.parent.path;
      final temp = Directory.systemTemp.createTempSync(
        'metadata-qualification-',
      );
      addTearDown(() => temp.deleteSync(recursive: true));
      final readiness =
          jsonDecode(
                File(
                  p.join(root, 'release', 'release-readiness.json'),
                ).readAsStringSync(),
              )
              as Map<String, dynamic>;
      readiness['status'] = 'ready';
      for (final gate in readiness['gates'] as List) {
        if (gate['enforcement'] != 'blocking') continue;
        gate['status'] = 'approved';
        gate.remove('reasonCode');
        gate['evidenceIds'] = ['EVID-${gate['id']}-0001'];
      }
      final tracked = File(p.join(temp.path, 'release/release-readiness.json'))
        ..createSync(recursive: true)
        ..writeAsStringSync(jsonEncode(readiness));
      const schemaPath = 'schemas/topiaforge.release-readiness-v1.schema.json';
      File(p.join(temp.path, schemaPath))
        ..createSync(recursive: true)
        ..writeAsBytesSync(File(p.join(root, schemaPath)).readAsBytesSync());
      String git(List<String> args) {
        final result = Process.runSync(
          'git',
          args,
          workingDirectory: temp.path,
        );
        expect(result.exitCode, 0, reason: '${result.stderr}');
        return '${result.stdout}'.trim();
      }

      git(['init', '--quiet']);
      git(['config', 'user.name', 'Qualification Regression']);
      git(['config', 'user.email', 'qualification@example.invalid']);
      git(['add', '--', tracked.path, schemaPath]);
      git(['commit', '--quiet', '-m', 'test: freeze synthetic approvals']);
      final sha = git(['rev-parse', 'HEAD']);
      await expectLater(
        ReleaseMetadataReadiness.load(
          repositoryRoot: temp.path,
          version: '0.1.0-rc.1',
          targetSha: sha,
          allowUnresolved: false,
        ),
        throwsStateError,
      );
    },
  );
}
