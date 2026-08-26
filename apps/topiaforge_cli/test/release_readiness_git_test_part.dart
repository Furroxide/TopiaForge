part of 'release_readiness_test.dart';

/// The git-backed half: readiness and schema are read from one exact commit, never the
/// working tree.
///
/// Split out to keep the suite inside the 500-line cap in AGENTS.md.
void _exactTargetCommitTests() {
  test(
    'loads readiness and schema only from the exact target commit',
    () async {
      final temp = Directory.systemTemp.createTempSync(
        'topiaforge-readiness-git-',
      );
      try {
        final readinessFile = File(p.join(temp.path, releaseReadinessPath))
          ..createSync(recursive: true)
          ..writeAsBytesSync(readinessBytes);
        File(
          p.join(temp.path, 'TopiaForge.slnx'),
        ).writeAsStringSync('<Solution />');
        File(p.join(temp.path, releaseReadinessSchemaPath))
          ..createSync(recursive: true)
          ..writeAsBytesSync(schemaBytes);
        _git(temp.path, ['init', '--quiet']);
        _git(temp.path, ['config', 'user.name', 'Release Readiness Test']);
        _git(temp.path, [
          'config',
          'user.email',
          'release-readiness@example.invalid',
        ]);
        _git(temp.path, ['add', '--', releaseReadinessPath]);
        _git(temp.path, ['add', '--', releaseReadinessSchemaPath]);
        _git(temp.path, ['commit', '--quiet', '-m', 'test: freeze readiness']);
        final commit = _git(temp.path, ['rev-parse', 'HEAD']).trim();

        final workingTreeDrift = _readinessJson(readinessBytes)
          ..['status'] = 'ready';
        readinessFile.writeAsStringSync(jsonEncode(workingTreeDrift));
        File(p.join(temp.path, releaseReadinessSchemaPath)).deleteSync();

        final decision = await ReleaseReadinessDecision.loadAtGitSha(
          repositoryRoot: temp.path,
          targetSha: commit,
          expectedReleaseVersion: '0.1.0-rc.1',
        );
        expect(decision.status, 'blocked');
        expect(decision.targetSha, commit);
        final metadataReadiness = await ReleaseMetadataReadiness.load(
          repositoryRoot: temp.path,
          version: '0.1.0-rc.1',
          targetSha: commit,
          allowUnresolved: true,
        );
        expect(metadataReadiness.status, 'blocked');
        expect(metadataReadiness.blobSha256, decision.readinessBlobSha256);
        expect(metadataReadiness.summary, decision.toPublicSummary());
        // Five blocking gates, not twelve: the advisory seven stay in the
        // summary but no longer make the candidate non-distributable.
        expect(metadataReadiness.blockingReasons, hasLength(5));
        expect(
          metadataReadiness.blockingReasons,
          isNot(contains(contains('P0-WIN-01'))),
        );
        final bomSchema = _readinessBomSchema(repositoryRoot);
        final bomSchemaResult = bomSchema.validate(
          metadataReadiness.toBomJson(),
        );
        expect(
          bomSchemaResult.isValid,
          isTrue,
          reason: bomSchemaResult.errors.join('\n'),
        );
        final tamperedBinding = metadataReadiness.toBomJson()
          ..['binding'] = 'working-tree';
        expect(bomSchema.validate(tamperedBinding).isValid, isFalse);
        await expectLater(
          ReleaseMetadataReadiness.load(
            repositoryRoot: temp.path,
            version: '0.1.0-rc.1',
            targetSha: commit,
            allowUnresolved: false,
          ),
          throwsA(
            isA<StateError>().having(
              (error) => error.message,
              'message',
              contains('Release readiness validation failed'),
            ),
          ),
        );

        final cli = await Process.run(Platform.resolvedExecutable, [
          p.join(
            repositoryRoot,
            'apps',
            'topiaforge_cli',
            'bin',
            'topiaforge.dart',
          ),
          'release',
          'validate-readiness',
          '--version',
          '0.1.0-rc.1',
          '--target-sha',
          commit,
        ], workingDirectory: temp.path);
        expect(
          cli.exitCode,
          1,
          reason: 'stdout: ${cli.stdout}\nstderr: ${cli.stderr}',
        );
        expect(cli.stderr, contains('P0-IP-01 is blocked'));

        _git(temp.path, [
          'rm',
          '--force',
          '--quiet',
          '--',
          releaseReadinessPath,
        ]);
        _git(temp.path, [
          'commit',
          '--quiet',
          '-m',
          'test: remove readiness decision',
        ]);
        final missingDecisionCommit = _git(temp.path, [
          'rev-parse',
          'HEAD',
        ]).trim();
        await expectLater(
          ReleaseReadinessDecision.loadAtGitSha(
            repositoryRoot: temp.path,
            targetSha: missingDecisionCommit,
            expectedReleaseVersion: '0.1.0-rc.1',
          ),
          throwsA(
            isA<StateError>().having(
              (error) => error.message,
              'message',
              contains('not tracked at the target commit'),
            ),
          ),
        );
        final unavailable = await ReleaseMetadataReadiness.load(
          repositoryRoot: temp.path,
          version: '0.1.0-rc.1',
          targetSha: missingDecisionCommit,
          allowUnresolved: true,
        );
        expect(unavailable.status, 'unavailable');
        expect(unavailable.blobSha256, isNull);
        expect(unavailable.summary, isNull);
      } finally {
        temp.deleteSync(recursive: true);
      }
    },
  );
}
