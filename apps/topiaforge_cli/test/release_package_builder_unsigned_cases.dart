part of 'release_package_builder_test.dart';

void _registerReleaseUnsignedBuilderTests() {
  _registerWindowsPolicyEvidenceTests();
  for (final existing in [false, true]) {
    test(
      'unsigned signing conflict refuses before writes existing=$existing',
      () async {
        final repo = _writeFixtureRepo(temp);
        _writeUnsignedPolicy(repo);
        final output = Directory(p.join(temp.path, 'unsigned-output'));
        final sentinel = File(
          p.join(output.path, 'TopiaForge-windows-x64', 'keep.txt'),
        );
        if (existing) {
          sentinel.createSync(recursive: true);
          sentinel.writeAsStringSync('previous candidate');
        }
        Object? failure;
        try {
          await ReleasePackageBuilder(
            repositoryRoot: repo.path,
            platform: ReleasePackagePlatform.windows,
            outputRoot: output.path,
            requireWindowsSigning: true,
            rebuildRuntimePayload: false,
            prebuiltLauncher: p.join(temp.path, 'missing-launcher'),
          ).build();
        } on Object catch (error) {
          failure = error;
        }
        expect(
          output.existsSync(),
          existing,
          reason: 'Signing admission precedes all writes.',
        );
        if (existing) expect(sentinel.readAsStringSync(), 'previous candidate');
        expect(failure.toString(), contains('unsigned'));
        expect(failure.toString(), contains('require-windows-signing'));
      },
    );
  }

  final boundaries =
      <
        ({
          String name,
          String mode,
          String? pin,
          String version,
          String diagnostic,
        })
      >[
        (
          name: 'invalid certificate pin',
          mode: 'unsigned',
          pin: 'invalid-pin',
          version: '0.1.0-rc.1',
          diagnostic: 'certificate',
        ),
        (
          name: 'zero certificate pin',
          mode: 'unsigned',
          pin: '0' * 64,
          version: '0.1.0-rc.1',
          diagnostic: 'certificate',
        ),
        (
          name: 'whitespace certificate pin',
          mode: 'unsigned',
          pin: ' \t ',
          version: '0.1.0-rc.1',
          diagnostic: 'certificate',
        ),
        (
          name: 'valid certificate pin',
          mode: 'unsigned',
          pin: 'a' * 64,
          version: '0.1.0-rc.1',
          diagnostic: 'certificate',
        ),
        (
          name: 'stable version',
          mode: 'unsigned',
          pin: null,
          version: '0.1.0',
          diagnostic: 'prerelease',
        ),
        (
          name: 'nonzero major version',
          mode: 'unsigned',
          pin: null,
          version: '1.0.0-rc.1',
          diagnostic: 'prerelease',
        ),
        (
          name: 'malformed version',
          mode: 'unsigned',
          pin: null,
          version: 'not-a-version',
          diagnostic: 'prerelease',
        ),
        (
          name: 'unknown mode',
          mode: 'maybe',
          pin: null,
          version: '0.1.0-rc.1',
          diagnostic: 'distribution mode',
        ),
        (
          name: 'case-invalid mode',
          mode: 'UNSIGNED',
          pin: null,
          version: '0.1.0-rc.1',
          diagnostic: 'distribution mode',
        ),
      ];
  for (final boundary in boundaries) {
    for (final existing in [false, true]) {
      test('Windows policy refuses ${boundary.name} before writes '
          'existing=$existing', () async {
        final repo = _writeFixtureRepo(temp);
        _writeUnsignedPolicy(
          repo,
          mode: boundary.mode,
          certificatePin: boundary.pin,
          version: boundary.version,
        );
        final output = Directory(p.join(temp.path, 'policy-output'));
        final sentinel = File(
          p.join(output.path, 'TopiaForge-windows-x64', 'keep.txt'),
        );
        final archive = File(p.join(output.path, 'TopiaForge-windows-x64.zip'));
        if (existing) {
          sentinel.createSync(recursive: true);
          sentinel.writeAsStringSync('previous candidate');
          archive.writeAsStringSync('previous archive');
        }
        final runner = _RecordingProcessRunner();
        Object? failure;
        try {
          await ReleasePackageBuilder(
            repositoryRoot: repo.path,
            platform: ReleasePackagePlatform.windows,
            outputRoot: output.path,
            rebuildRuntimePayload: false,
            prebuiltLauncher: p.join(temp.path, 'missing-launcher'),
            processRunner: runner,
          ).build();
        } on Object catch (error) {
          failure = error;
        }
        expect(
          output.existsSync(),
          existing,
          reason: 'Policy admission precedes all writes.',
        );
        if (existing) {
          expect(
            sentinel.existsSync(),
            isTrue,
            reason: 'Refusal must preserve the prior staged candidate.',
          );
          expect(sentinel.readAsStringSync(), 'previous candidate');
          expect(archive.readAsStringSync(), 'previous archive');
        }
        expect(runner.calls, isEmpty);
        expect(failure, isA<StateError>());
        expect(failure.toString(), contains(boundary.diagnostic));
      });
    }
  }

  for (final mode in <String?>[null, 'signed']) {
    test('Windows policy preserves ${mode ?? 'implicit'} signed admission '
        'for a stable version', () async {
      final repo = _writeFixtureRepo(temp);
      _writeUnsignedPolicy(
        repo,
        mode: mode,
        certificatePin: 'a' * 64,
        version: '1.2.3',
      );
      final output = Directory(p.join(temp.path, 'signed-output'));
      // Reaching launcher validation proves the unsigned-only constraints did
      // not reject signed policy. The existing signer cases check all three
      // signatures, timestamping, signer identity, and missing credentials.
      await expectLater(
        ReleasePackageBuilder(
          repositoryRoot: repo.path,
          platform: ReleasePackagePlatform.windows,
          outputRoot: output.path,
          requireWindowsSigning: true,
          rebuildRuntimePayload: false,
          prebuiltLauncher: p.join(temp.path, 'missing-launcher'),
        ).build(),
        throwsA(
          isA<StateError>().having(
            (error) => error.message,
            'message',
            contains('Prebuilt launcher was not found'),
          ),
        ),
      );
      expect(output.existsSync(), isTrue);
    });
  }

  test(
    'unsigned builder ignores ambient signing credentials',
    () async {
      if (Platform.environment['TOPIAFORGE_UNSIGNED_BUILDER_CHILD'] != 'true') {
        final result = await Process.run(
          Platform.resolvedExecutable,
          [
            'test',
            'test/release_package_builder_test.dart',
            '--name',
            r'^unsigned builder ignores ambient signing credentials$',
            '-r',
            'expanded',
          ],
          workingDirectory: Directory.current.path,
          environment: {
            'TOPIAFORGE_UNSIGNED_BUILDER_CHILD': 'true',
            'WINDOWS_CERTIFICATE_PFX': base64Encode([1, 2, 3, 4]),
            'WINDOWS_CERTIFICATE_PASSWORD': 'synthetic-test-value',
            'WINDOWS_TIMESTAMP_URL': 'https://timestamp.example.test/rfc3161',
          },
        );
        expect(
          result.exitCode,
          0,
          reason: '${result.stdout}\n${result.stderr}',
        );
        return;
      }
      final repo = _writeFixtureRepo(temp);
      _writeUnsignedPolicy(repo);
      final launcher = Directory(p.join(temp.path, 'unsigned-launcher'))
        ..createSync();
      _writeFile(launcher, ['topiaforge_launcher.exe'], 'launcher fixture');
      _writeFile(launcher, [
        'data',
        'flutter_assets',
        'NOTICES.Z',
      ], 'Flutter notices');
      final cli = File(p.join(temp.path, 'unsigned-cli.exe'))
        ..writeAsStringSync('cli fixture');
      final runner = _RecordingProcessRunner(availableCommands: {'signtool'});
      await ReleasePackageBuilder(
        repositoryRoot: repo.path,
        platform: ReleasePackagePlatform.windows,
        outputRoot: p.join(temp.path, 'unsigned-output'),
        prebuiltLauncher: launcher.path,
        prebuiltCli: cli.path,
        rebuildRuntimePayload: false,
        processRunner: runner,
      ).build();
      expect(
        runner.calls.where((call) => call.executable == 'signtool'),
        isEmpty,
      );
    },
    skip: !Platform.isWindows
        ? 'Windows Authenticode environment behavior.'
        : false,
  );
}

void _writeUnsignedPolicy(
  Directory repo, {
  String? mode = 'unsigned',
  String? certificatePin,
  String version = '0.1.0-rc.1',
}) {
  var source = Directory.current.absolute;
  while (!File(p.join(source.path, 'TopiaForge.slnx')).existsSync()) {
    source = source.parent;
  }
  final policy =
      jsonDecode(
            File(
              p.join(source.path, 'release', 'release-policy.json'),
            ).readAsStringSync(),
          )
          as Map<String, Object?>;
  policy['signingIdentities'] = {
    'windowsDistribution': ?mode,
    'windowsCertificateSha256': ?certificatePin,
  };
  (policy['versioning']! as Map<String, Object?>)['productVersion'] = version;
  _writeFile(repo, ['release', 'release-policy.json'], jsonEncode(policy));
}
