part of 'release_package_builder_test.dart';

void _registerWindowsPolicyEvidenceTests() {
  final malformed = <({String name, String field, Object? value})>[
    (name: 'null mode', field: 'windowsDistribution', value: null),
    (name: 'scalar mode', field: 'windowsDistribution', value: 7),
    (name: 'null certificate', field: 'windowsCertificateSha256', value: null),
    (name: 'empty certificate', field: 'windowsCertificateSha256', value: ''),
    (name: 'scalar certificate', field: 'windowsCertificateSha256', value: 7),
  ];
  for (final value in malformed) {
    test('Windows raw policy rejects ${value.name} before writes', () async {
      final repo = _writeFixtureRepo(temp);
      _writeUnsignedPolicy(repo);
      final policy = File(p.join(repo.path, 'release', 'release-policy.json'));
      final json =
          jsonDecode(policy.readAsStringSync()) as Map<String, Object?>;
      (json['signingIdentities']! as Map<String, Object?>)[value.field] =
          value.value;
      policy.writeAsStringSync(jsonEncode(json));
      await _expectPolicyRefusalWithoutWrites(repo);
    });
  }

  for (final version in [
    '0.1.0-rc.1\n',
    ' 0.1.0-rc.1',
    '00.1.0-rc.1',
    '0.01.0-rc.1',
    '0.1.0-01',
    '0.1.0-rc..1',
    '0.1.0-rélease',
    '0.1.0-rc.1+',
  ]) {
    test(
      'Windows unsigned admission rejects non-SemVer ${jsonEncode(version)}',
      () async {
        final repo = _writeFixtureRepo(temp);
        _writeUnsignedPolicy(repo, version: version);
        await _expectPolicyRefusalWithoutWrites(repo);
      },
    );
  }

  for (final version in ['0.1.0-0', '0.1.0-rc.1+build.01']) {
    test(
      'Windows unsigned admission preserves valid SemVer $version',
      () async {
        final repo = _writeFixtureRepo(temp);
        _writeUnsignedPolicy(repo, version: version);
        await expectLater(
          ReleasePackageBuilder(
            repositoryRoot: repo.path,
            platform: ReleasePackagePlatform.windows,
            outputRoot: p.join(temp.path, 'valid-semver-output'),
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
      },
    );
  }

  for (final initialMode in ['signed', 'unsigned']) {
    final name =
        'Windows $initialMode policy snapshot survives async replacement';
    test(
      name,
      () async {
        final marker =
            'TOPIAFORGE_POLICY_SNAPSHOT_${initialMode.toUpperCase()}';
        if (await _runSyntheticSigningChild(name, marker)) return;
        final repo = _writeFixtureRepo(temp);
        final pin = 'a' * 64;
        _writeUnsignedPolicy(
          repo,
          mode: initialMode,
          certificatePin: initialMode == 'signed' ? pin : null,
        );
        final cli = File(p.join(temp.path, 'snapshot-cli.exe'))
          ..writeAsStringSync('CLI fixture');
        final output = Directory(p.join(temp.path, 'snapshot-output'));
        var replaced = false;
        final runner = _RecordingProcessRunner(
          availableCommands: {'flutter', 'signtool'},
          onRun: (call) async {
            if (call.executable != 'flutter.bat') return;
            await Future<void>.delayed(Duration.zero);
            _writeUnsignedPolicy(
              repo,
              mode: initialMode == 'signed' ? 'unsigned' : 'signed',
              certificatePin: initialMode == 'unsigned' ? 'b' * 64 : null,
            );
            replaced = true;
            final launcher = Directory(
              p.join(
                repo.path,
                'apps',
                'topiaforge_launcher_flutter',
                'build',
                'windows',
                'x64',
                'runner',
                'Release',
              ),
            );
            _writeFile(launcher, [
              'topiaforge_launcher.exe',
            ], 'launcher fixture');
            _writeFile(launcher, [
              'data',
              'flutter_assets',
              'NOTICES.Z',
            ], 'notices');
            _writeFile(output, [
              'TopiaForge-windows-x64',
              'TopiaForge.GameCompat.Extractor.exe',
            ], 'extractor fixture');
          },
          onResult: (call) => call.executable == 'powershell.exe'
              ? ProcessResult(1, 0, pin, '')
              : ProcessResult(1, 1, '', 'unexpected process'),
        );
        final archive = await ReleasePackageBuilder(
          repositoryRoot: repo.path,
          platform: ReleasePackagePlatform.windows,
          outputRoot: output.path,
          requireWindowsSigning: initialMode == 'signed',
          prebuiltCli: cli.path,
          rebuildRuntimePayload: false,
          processRunner: runner,
        ).build();
        expect(
          replaced,
          isTrue,
          reason: 'The policy changed during an awaited build.',
        );
        expect(File(archive).existsSync(), isTrue);
        final signCalls = runner.calls.where(
          (call) =>
              call.executable == 'signtool' && call.arguments.first == 'sign',
        );
        final verifyCalls = runner.calls.where(
          (call) =>
              call.executable == 'signtool' && call.arguments.first == 'verify',
        );
        expect(signCalls, hasLength(initialMode == 'signed' ? 3 : 0));
        expect(verifyCalls, hasLength(initialMode == 'signed' ? 3 : 0));
      },
      skip: !Platform.isWindows
          ? 'Windows Authenticode policy snapshot.'
          : false,
    );
  }
}

Future<void> _expectPolicyRefusalWithoutWrites(Directory repo) async {
  final output = Directory(p.join(temp.path, 'raw-policy-output'));
  final sentinel = File(
    p.join(output.path, 'TopiaForge-windows-x64', 'keep.txt'),
  );
  sentinel.createSync(recursive: true);
  sentinel.writeAsStringSync('previous candidate');
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
    sentinel.existsSync(),
    isTrue,
    reason: 'Malformed policy cannot delete the previous candidate.',
  );
  expect(sentinel.readAsStringSync(), 'previous candidate');
  expect(runner.calls, isEmpty);
  expect(failure, isNotNull);
  expect(
    failure.toString(),
    isNot(contains('Prebuilt launcher was not found')),
  );
}

Future<bool> _runSyntheticSigningChild(String testName, String marker) async {
  if (Platform.environment[marker] == 'true') return false;
  final result = await Process.run(
    Platform.resolvedExecutable,
    [
      'test',
      'test/release_package_builder_test.dart',
      '--name',
      '^${RegExp.escape(testName)}\$',
      '--reporter',
      'expanded',
    ],
    workingDirectory: Directory.current.path,
    environment: {
      marker: 'true',
      'WINDOWS_CERTIFICATE_PFX': base64Encode([1, 2, 3, 4]),
      'WINDOWS_CERTIFICATE_PASSWORD': 'synthetic-test-value',
      'WINDOWS_TIMESTAMP_URL': 'https://timestamp.example.test/rfc3161',
    },
  );
  expect(result.exitCode, 0, reason: '${result.stdout}\n${result.stderr}');
  return true;
}
