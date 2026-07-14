part of 'release_package_builder_test.dart';

void _registerReleaseProcessAndIoTests() {
  test('redacts secret-bearing process arguments in command logs', () {
    final logLine = const ReleaseProcessRunner().formatCommandForLog(
      'security',
      [
        'import',
        'cert.p12',
        '-P',
        'certificate-secret',
        '-p',
        'keychain-secret',
        '-k',
        'partition-secret',
        '--password',
        'notary-secret',
        '--team-id',
        'TEAMID',
      ],
      redactedValueOptions: const {'-P', '-p', '-k', '--password'},
    );

    expect(logLine, contains('-P <redacted>'));
    expect(logLine, contains('-p <redacted>'));
    expect(logLine, contains('-k <redacted>'));
    expect(logLine, contains('--password <redacted>'));
    expect(logLine, contains('--team-id TEAMID'));
    expect(logLine, isNot(contains('certificate-secret')));
    expect(logLine, isNot(contains('keychain-secret')));
    expect(logLine, isNot(contains('partition-secret')));
    expect(logLine, isNot(contains('notary-secret')));
  });

  test('strips release credentials from every child process environment', () {
    final environment = releaseChildEnvironment({
      'SAFE_VALUE': 'visible',
      'GITHUB_TOKEN': 'github-secret',
      'MACOS_CERTIFICATE_PASSWORD': 'certificate-secret',
      'MACOS_NOTARY_PASSWORD': 'notary-secret',
      'ROBOTOPIA_REFS_TOKEN': 'refs-secret',
      'OPENAI_API_KEY': 'api-secret',
      'AWS_SECRET_ACCESS_KEY': 'cloud-secret',
      'DATABASE_URL': 'postgres://user:password@example.test/database',
      'SSH_AUTH_SOCK': '/tmp/agent.sock',
    });

    expect(environment['SAFE_VALUE'], 'visible');
    expect(environment, isNot(contains('GITHUB_TOKEN')));
    expect(environment, isNot(contains('MACOS_CERTIFICATE_PASSWORD')));
    expect(environment, isNot(contains('MACOS_NOTARY_PASSWORD')));
    expect(environment, isNot(contains('ROBOTOPIA_REFS_TOKEN')));
    expect(environment, isNot(contains('OPENAI_API_KEY')));
    expect(environment, isNot(contains('AWS_SECRET_ACCESS_KEY')));
    expect(environment, isNot(contains('DATABASE_URL')));
    expect(environment, isNot(contains('SSH_AUTH_SOCK')));
    expect(environment, isNot(contains('WINDOWS_CERTIFICATE_PFX')));
    expect(environment, isNot(contains('WINDOWS_CERTIFICATE_PASSWORD')));
  });

  test(
    'Windows release signer signs, timestamps, and verifies executables',
    () async {
      final stage = Directory(p.join(temp.path, 'windows-stage'))..createSync();
      for (final relative in [
        'robotopia.exe',
        'Robotopia.GameCompat.Extractor.exe',
        p.join('launcher', 'robotopia_launcher_flutter.exe'),
      ]) {
        _writeFile(stage, p.split(relative), 'portable executable fixture');
      }
      final runner = _RecordingProcessRunner(availableCommands: {'signtool'});

      await WindowsPackageSigner(
        processRunner: runner,
        requireTrustedSignature: true,
        isWindows: true,
        environment: {
          'WINDOWS_CERTIFICATE_PFX': base64Encode([1, 2, 3, 4]),
          'WINDOWS_CERTIFICATE_PASSWORD': 'certificate-secret',
          'WINDOWS_TIMESTAMP_URL': 'https://timestamp.example.test/rfc3161',
        },
      ).signIfConfigured(stage.path);

      final signCalls = runner.calls
          .where((call) => call.arguments.first == 'sign')
          .toList();
      final verifyCalls = runner.calls
          .where((call) => call.arguments.first == 'verify')
          .toList();
      expect(signCalls, hasLength(3));
      expect(verifyCalls, hasLength(3));
      for (final call in signCalls) {
        expect(call.executable, 'signtool');
        expect(call.arguments, containsAll(['/fd', 'SHA256', '/td']));
        expect(
          call.arguments,
          containsAllInOrder([
            '/tr',
            'https://timestamp.example.test/rfc3161',
            '/td',
            'SHA256',
          ]),
        );
        expect(call.arguments, containsAll(['/p', 'certificate-secret']));
      }
      for (final call in verifyCalls) {
        expect(call.arguments, containsAll(['/pa', '/all', '/tw', '/v']));
      }
    },
  );

  test('public Windows signing fails closed without credentials', () async {
    await expectLater(
      () => WindowsPackageSigner(
        requireTrustedSignature: true,
        isWindows: true,
        environment: const {},
      ).signIfConfigured(temp.path),
      throwsA(isA<StateError>()),
    );
  });

  test('Dart zip extraction rejects symlinks before writing files', () async {
    final zip = File(p.join(temp.path, 'symlink.zip'));
    final link = ArchiveFile.string('link', '../outside')..mode = 0xa1ff;
    final archive = Archive()
      ..addFile(ArchiveFile.string('safe.txt', 'safe'))
      ..addFile(link);
    zip.writeAsBytesSync(_markZipEntriesAsUnix(ZipEncoder().encode(archive)));

    final extracted = Directory(p.join(temp.path, 'extracted'));
    await expectLater(
      () => const ReleaseFileOps().extractPlatformZip(
        zip,
        extracted,
        ReleasePackagePlatform.windows,
      ),
      throwsA(isA<StateError>()),
    );
    expect(File(p.join(extracted.path, 'safe.txt')).existsSync(), false);
  });

  test('Dart zip extraction rejects traversal before writing files', () async {
    final zip = File(p.join(temp.path, 'traversal.zip'));
    final archive = Archive()
      ..addFile(ArchiveFile.string('safe.txt', 'safe'))
      ..addFile(ArchiveFile.string('../outside.txt', 'outside'));
    zip.writeAsBytesSync(ZipEncoder().encode(archive));

    final extracted = Directory(p.join(temp.path, 'extracted'));
    await expectLater(
      () => const ReleaseFileOps().extractPlatformZip(
        zip,
        extracted,
        ReleasePackagePlatform.windows,
      ),
      throwsA(isA<StateError>()),
    );
    expect(File(p.join(extracted.path, 'safe.txt')).existsSync(), false);
    expect(File(p.join(temp.path, 'outside.txt')).existsSync(), false);
  });

  test('release zip writer is deterministic and atomic', () async {
    final runner = _RecordingProcessRunner(
      availableCommands: {'tar', 'zip', '/usr/bin/ditto'},
    );
    final source = Directory(p.join(temp.path, 'source'))..createSync();
    final payload = File(p.join(source.path, 'nested', 'payload.txt'))
      ..parent.createSync(recursive: true)
      ..writeAsStringSync('payload');
    final first = File(p.join(temp.path, 'out', 'first.zip'));
    final second = File(p.join(temp.path, 'out', 'second.zip'));
    final replacement = File(p.join(temp.path, 'out', 'replace.zip'))
      ..parent.createSync(recursive: true)
      ..writeAsStringSync('old package must survive until replacement');
    final fileOps = ReleaseFileOps(processRunner: runner);

    await fileOps.writePlatformZip(
      source,
      first,
      ReleasePackagePlatform.windows,
    );
    payload.setLastModifiedSync(DateTime.now());
    await fileOps.writePlatformZip(
      source,
      second,
      ReleasePackagePlatform.windows,
    );
    await fileOps.writePlatformZip(
      source,
      replacement,
      ReleasePackagePlatform.windows,
    );

    expect(first.readAsBytesSync(), second.readAsBytesSync());
    expect(first.readAsBytesSync(), replacement.readAsBytesSync());
    final decoded = ZipDecoder().decodeBytes(first.readAsBytesSync());
    expect(decoded.files.where((entry) => entry.isDirectory), isEmpty);
    expect(decoded.files.map((entry) => entry.name), ['nested/payload.txt']);
    expect(decoded.files.single.mode & 0xf000, 0x8000);
    expect(runner.calls, isEmpty);
    expect(
      replacement.parent.listSync().where(
        (entry) => p.basename(entry.path).contains('.tmp-'),
      ),
      isEmpty,
    );
  });
}
