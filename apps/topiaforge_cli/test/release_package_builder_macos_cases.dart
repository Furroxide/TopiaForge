part of 'release_package_builder_test.dart';

void _registerReleaseMacPackagingTests() {
  test('ad-hoc macOS dry-run signing disables hardened runtime', () async {
    final runner = _RecordingProcessRunner(availableCommands: {'codesign'});

    await MacPackageSigner(
      processRunner: runner,
      isMacOS: true,
      environment: const {},
    ).signIfConfigured(p.join(temp.path, 'TopiaForge.app'), temp.path);

    final signCall = runner.calls.singleWhere(
      (call) =>
          call.executable == 'codesign' && call.arguments.contains('--sign'),
    );
    expect(
      signCall.arguments,
      containsAll(['--force', '--deep', '--sign', '-']),
    );
    expect(signCall.arguments, isNot(contains('--options')));
    expect(signCall.arguments, isNot(contains('--timestamp')));
    expect(
      runner.calls.any(
        (call) =>
            call.executable == 'codesign' &&
            ['--verify', '--deep', '--strict'].every(call.arguments.contains),
      ),
      isTrue,
    );
  });

  test('Developer ID macOS signing retains hardened runtime', () async {
    final runner = _RecordingProcessRunner(availableCommands: {'codesign'});

    await MacPackageSigner(
      processRunner: runner,
      isMacOS: true,
      environment: {
        'MACOS_CERTIFICATE_P12': base64Encode([1, 2, 3, 4]),
        'MACOS_CERTIFICATE_PASSWORD': 'certificate-secret',
        'MACOS_DEVELOPER_ID_APPLICATION':
            'Developer ID Application: TopiaForge (TEAMID1234)',
      },
    ).signIfConfigured(p.join(temp.path, 'TopiaForge.app'), temp.path);

    final signCall = runner.calls.singleWhere(
      (call) =>
          call.executable == 'codesign' && call.arguments.contains('--sign'),
    );
    expect(
      signCall.arguments,
      containsAll([
        '--force',
        '--options',
        'runtime',
        '--timestamp',
        '--deep',
        '--sign',
        'Developer ID Application: TopiaForge (TEAMID1234)',
      ]),
    );
  });

  test('public macOS signing fails closed without credentials', () async {
    await expectLater(
      () => MacPackageSigner(
        processRunner: _RecordingProcessRunner(availableCommands: {'codesign'}),
        requireTrustedSignature: true,
        isMacOS: true,
        environment: const {},
      ).signIfConfigured(p.join(temp.path, 'TopiaForge.app'), temp.path),
      throwsA(isA<StateError>()),
    );
  });

  test('macOS technical packaging fails closed without codesign', () async {
    await expectLater(
      () => MacPackageSigner(
        processRunner: _RecordingProcessRunner(),
        isMacOS: true,
        environment: const {},
      ).signIfConfigured(p.join(temp.path, 'TopiaForge.app'), temp.path),
      throwsA(isA<StateError>()),
    );
  });

  test('macOS technical packaging fails when ad-hoc signing fails', () async {
    await expectLater(
      () => MacPackageSigner(
        processRunner: _RecordingProcessRunner(
          availableCommands: {'codesign'},
          onRun: (call) async {
            if (call.arguments.contains('--sign')) {
              throw StateError('fixture signing failure');
            }
          },
        ),
        isMacOS: true,
        environment: const {},
      ).signIfConfigured(p.join(temp.path, 'TopiaForge.app'), temp.path),
      throwsA(
        isA<StateError>().having(
          (error) => error.toString(),
          'message',
          contains('refusing to emit'),
        ),
      ),
    );
  });

  test('macOS packages dispatch to separate runnable AOT binaries', () async {
    final repo = _writeFixtureRepo(temp);
    final launcher = Directory(p.join(temp.path, 'TopiaForge.app'));
    _writeFile(launcher, [
      'Contents',
      'MacOS',
      'topiaforge_launcher',
    ], 'launcher');
    _writeFile(launcher, [
      'Contents',
      'Frameworks',
      'App.framework',
      'Resources',
      'flutter_assets',
      'NOTICES.Z',
    ], 'Flutter notices');
    final cliPair = Directory(p.join(temp.path, 'mac-cli'))
      ..createSync(recursive: true);
    _writeFile(cliPair, [macCliArm64FileName], 'arm64 AOT snapshot');
    _writeFile(cliPair, [macCliX64FileName], 'x64 AOT snapshot');
    final runner = _RecordingProcessRunner(availableCommands: {'codesign'});

    final zipPath = await ReleasePackageBuilder(
      repositoryRoot: repo.path,
      platform: ReleasePackagePlatform.macos,
      outputRoot: p.join(temp.path, 'mac-out'),
      prebuiltLauncher: launcher.path,
      prebuiltCli: cliPair.path,
      rebuildRuntimePayload: false,
      processRunner: runner,
    ).build();

    final extracted = Directory(p.join(temp.path, 'mac-extracted'));
    await const ReleaseFileOps().extractPlatformZip(
      File(zipPath),
      extracted,
      ReleasePackagePlatform.macos,
    );
    final payload = p.join(
      extracted.path,
      'TopiaForge.app',
      'Contents',
      'Resources',
      'TopiaForge',
    );
    expect(
      File(p.join(payload, macCliArm64FileName)).readAsStringSync(),
      'arm64 AOT snapshot',
    );
    expect(
      File(p.join(payload, macCliX64FileName)).readAsStringSync(),
      'x64 AOT snapshot',
    );
    final dispatcher = File(p.join(payload, 'topiaforge')).readAsStringSync();
    expect(dispatcher, startsWith('#!/bin/sh\n'));
    expect(dispatcher, contains(macCliArm64FileName));
    expect(dispatcher, contains(macCliX64FileName));
    expect(runner.calls.where((call) => call.executable == 'lipo'), isEmpty);
  });

  test('macOS packaging rejects a single lipo-style CLI executable', () async {
    final repo = _writeFixtureRepo(temp);
    final launcher = Directory(p.join(temp.path, 'TopiaForge.app'));
    _writeFile(launcher, [
      'Contents',
      'MacOS',
      'topiaforge_launcher',
    ], 'launcher');
    final singleCli = File(p.join(temp.path, 'topiaforge'))
      ..writeAsStringSync('combined AOT executable');

    await expectLater(
      () => ReleasePackageBuilder(
        repositoryRoot: repo.path,
        platform: ReleasePackagePlatform.macos,
        outputRoot: p.join(temp.path, 'mac-out'),
        prebuiltLauncher: launcher.path,
        prebuiltCli: singleCli.path,
        rebuildRuntimePayload: false,
        processRunner: _RecordingProcessRunner(),
      ).build(),
      throwsA(
        isA<StateError>().having(
          (error) => error.toString(),
          'message',
          allOf(contains('directory'), contains('lipo')),
        ),
      ),
    );
  });
}
