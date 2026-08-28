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

  test('release smoke process capture is output-bounded', () async {
    final probe = p.join(
      Directory.current.path,
      'test',
      'fixtures',
      'game_compat_probe.dart',
    );

    await expectLater(
      const ReleaseProcessRunner().runBoundedResult(
        Platform.resolvedExecutable,
        [probe],
        environment: const {'TOPIAFORGE_GAME_COMPAT_PROBE_MODE': 'overflow'},
        timeout: const Duration(seconds: 30),
        maxStdoutBytes: 128 * 1024,
        maxStderrBytes: 128 * 1024,
      ),
      throwsA(
        isA<BoundedProcessException>().having(
          (error) => error.failure,
          'failure',
          BoundedProcessFailure.stdoutLimitExceeded,
        ),
      ),
    );
  });

  test(
    'Windows release signer signs, timestamps, and verifies executables',
    () async {
      final stage = Directory(p.join(temp.path, 'windows-stage'))..createSync();
      for (final relative in [
        'topiaforge.exe',
        'TopiaForge.GameCompat.Extractor.exe',
        p.join('launcher', 'topiaforge_launcher.exe'),
      ]) {
        _writeFile(stage, p.split(relative), 'portable executable fixture');
      }
      final expectedSigner = List.filled(64, 'a').join();
      final runner = _RecordingProcessRunner(
        availableCommands: {'signtool'},
        onResult: (call) => call.executable == 'powershell.exe'
            ? ProcessResult(1, 0, expectedSigner, '')
            : ProcessResult(1, 1, '', 'unexpected process'),
      );

      await WindowsPackageSigner(
        processRunner: runner,
        requireTrustedSignature: true,
        expectedSignerCertificateSha256: expectedSigner,
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

  test('public Windows signing requires an explicit timestamp URL', () async {
    final stage = Directory(p.join(temp.path, 'windows-timestamp-stage'))
      ..createSync();
    for (final relative in [
      'topiaforge.exe',
      'TopiaForge.GameCompat.Extractor.exe',
      p.join('launcher', 'topiaforge_launcher.exe'),
    ]) {
      _writeFile(stage, p.split(relative), 'portable executable fixture');
    }
    await expectLater(
      () => WindowsPackageSigner(
        processRunner: _RecordingProcessRunner(availableCommands: {'signtool'}),
        requireTrustedSignature: true,
        expectedSignerCertificateSha256: List.filled(64, 'a').join(),
        isWindows: true,
        environment: {
          'WINDOWS_CERTIFICATE_PFX': base64Encode([1, 2, 3, 4]),
          'WINDOWS_CERTIFICATE_PASSWORD': 'certificate-secret',
        },
      ).signIfConfigured(stage.path),
      throwsA(
        isA<StateError>().having(
          (error) => error.message,
          'message',
          contains('WINDOWS_TIMESTAMP_URL is mandatory'),
        ),
      ),
    );
  });

  test(
    'Windows trust verification binds every executable to reviewed signer',
    () async {
      final stage = Directory(p.join(temp.path, 'windows-verify-stage'))
        ..createSync();
      for (final relative in [
        'topiaforge.exe',
        'TopiaForge.GameCompat.Extractor.exe',
        p.join('launcher', 'topiaforge_launcher.exe'),
      ]) {
        _writeFile(stage, p.split(relative), 'signed executable fixture');
      }
      final expectedSigner = List.filled(64, 'a').join();
      final runner = _RecordingProcessRunner(
        availableCommands: {'signtool'},
        onResult: (call) => call.executable == 'powershell.exe'
            ? ProcessResult(1, 0, expectedSigner.toUpperCase(), '')
            : ProcessResult(1, 1, '', 'unexpected process'),
      );

      await WindowsPackageSigner(
        processRunner: runner,
        requireTrustedSignature: true,
        expectedSignerCertificateSha256: expectedSigner,
        isWindows: true,
      ).verifyTrustedSignatures(stage.path);

      expect(
        runner.calls.where((call) => call.executable == 'signtool'),
        hasLength(3),
      );
      expect(
        runner.calls.where((call) => call.executable == 'powershell.exe'),
        hasLength(3),
      );
    },
  );

  test('Windows trust verification rejects a different signer', () async {
    final stage = Directory(p.join(temp.path, 'windows-wrong-signer-stage'))
      ..createSync();
    for (final relative in [
      'topiaforge.exe',
      'TopiaForge.GameCompat.Extractor.exe',
      p.join('launcher', 'topiaforge_launcher.exe'),
    ]) {
      _writeFile(stage, p.split(relative), 'signed executable fixture');
    }
    final runner = _RecordingProcessRunner(
      availableCommands: {'signtool'},
      onResult: (_) => ProcessResult(1, 0, List.filled(64, 'b').join(), ''),
    );

    await expectLater(
      () => WindowsPackageSigner(
        processRunner: runner,
        requireTrustedSignature: true,
        expectedSignerCertificateSha256: List.filled(64, 'a').join(),
        isWindows: true,
      ).verifyTrustedSignatures(stage.path),
      throwsA(
        isA<StateError>().having(
          (error) => error.toString(),
          'message',
          contains('does not match the reviewed release policy'),
        ),
      ),
    );
  });

  test(
    'Windows unsigned verification checks every packaged executable',
    () async {
      final stage = Directory(p.join(temp.path, 'windows-unsigned-stage'))
        ..createSync();
      for (final relative in [
        'topiaforge.exe',
        'TopiaForge.GameCompat.Extractor.exe',
        p.join('launcher', 'topiaforge_launcher.exe'),
      ]) {
        _writeFile(stage, p.split(relative), 'unsigned executable fixture');
      }
      final runner = _RecordingProcessRunner(
        onResult: (call) => call.executable == 'powershell.exe'
            ? ProcessResult(1, 0, 'NotSigned|none|none', '')
            : ProcessResult(1, 1, '', 'unexpected process'),
      );

      await WindowsPackageSigner(
        processRunner: runner,
        isWindows: true,
      ).verifyUnsignedExecutables(stage.path);

      expect(
        runner.calls.where((call) => call.executable == 'powershell.exe'),
        hasLength(3),
      );
    },
  );

  test(
    'Windows unsigned verification rejects signed or invalid bytes',
    () async {
      final stage = Directory(p.join(temp.path, 'windows-signed-stage'))
        ..createSync();
      for (final relative in [
        'topiaforge.exe',
        'TopiaForge.GameCompat.Extractor.exe',
        p.join('launcher', 'topiaforge_launcher.exe'),
      ]) {
        _writeFile(stage, p.split(relative), 'signed executable fixture');
      }
      final runner = _RecordingProcessRunner(
        onResult: (_) => ProcessResult(1, 0, 'Valid|signer|stamp', ''),
      );

      await expectLater(
        () => WindowsPackageSigner(
          processRunner: runner,
          isWindows: true,
        ).verifyUnsignedExecutables(stage.path),
        throwsA(
          isA<StateError>().having(
            (error) => error.toString(),
            'message',
            contains('carries a signature: status Valid'),
          ),
        ),
      );
    },
  );

  test(
    'Windows unsigned verification rejects an untrusted signature at once',
    () async {
      // Windows reports a self-signed or otherwise untrusted certificate as
      // UnknownError, the same status it uses for a file it could not read.
      // The signer certificate is what separates them, and a real signature
      // must not be retried as though it were a transient read.
      final stage = Directory(p.join(temp.path, 'windows-untrusted-stage'))
        ..createSync();
      for (final relative in [
        'topiaforge.exe',
        'TopiaForge.GameCompat.Extractor.exe',
        p.join('launcher', 'topiaforge_launcher.exe'),
      ]) {
        _writeFile(stage, p.split(relative), 'untrusted executable fixture');
      }
      final runner = _RecordingProcessRunner(
        onResult: (_) => ProcessResult(1, 0, 'UnknownError|signer|none', ''),
      );

      await expectLater(
        () => WindowsPackageSigner(
          processRunner: runner,
          isWindows: true,
        ).verifyUnsignedExecutables(stage.path),
        throwsA(
          isA<StateError>().having(
            (error) => error.toString(),
            'message',
            contains('carries a signature: status UnknownError'),
          ),
        ),
      );
      // One probe per executable and no retries: the answer was conclusive.
      expect(
        runner.calls.where((call) => call.executable == 'powershell.exe'),
        hasLength(1),
      );
    },
  );

  test(
    'Windows unsigned verification retries a file it could not read',
    () async {
      // A freshly extracted executable is routinely held open by the antivirus
      // scanner for a moment. That is not a signing violation, and the check
      // used to report it as one.
      final stage = Directory(p.join(temp.path, 'windows-locked-stage'))
        ..createSync();
      for (final relative in [
        'topiaforge.exe',
        'TopiaForge.GameCompat.Extractor.exe',
        p.join('launcher', 'topiaforge_launcher.exe'),
      ]) {
        _writeFile(stage, p.split(relative), 'locked executable fixture');
      }
      var probes = 0;
      final runner = _RecordingProcessRunner(
        onResult: (_) {
          probes++;
          // Unreadable once per executable, then readable.
          return probes.isOdd
              ? ProcessResult(1, 0, 'unreadable:IOException', '')
              : ProcessResult(1, 0, 'NotSigned|none|none', '');
        },
      );

      await WindowsPackageSigner(
        processRunner: runner,
        isWindows: true,
      ).verifyUnsignedExecutables(stage.path);

      // Three executables, each needing a second probe.
      expect(
        runner.calls.where((call) => call.executable == 'powershell.exe'),
        hasLength(6),
      );
    },
  );

  test(
    'Windows unsigned verification does not call a persistent read failure signed',
    () async {
      final stage = Directory(p.join(temp.path, 'windows-unreadable-stage'))
        ..createSync();
      for (final relative in [
        'topiaforge.exe',
        'TopiaForge.GameCompat.Extractor.exe',
        p.join('launcher', 'topiaforge_launcher.exe'),
      ]) {
        _writeFile(stage, p.split(relative), 'unreadable executable fixture');
      }
      final runner = _RecordingProcessRunner(
        onResult: (_) => ProcessResult(1, 0, 'UnknownError|none|none', ''),
      );

      await expectLater(
        () => WindowsPackageSigner(
          processRunner: runner,
          isWindows: true,
        ).verifyUnsignedExecutables(stage.path),
        throwsA(
          isA<StateError>().having(
            (error) => error.toString(),
            'message',
            allOf(
              contains('Could not determine'),
              contains('not a signing violation'),
            ),
          ),
        ),
      );
    },
  );

  test(
    'package validator rejects contradictory Windows trust options',
    () async {
      await expectLater(
        () => ReleasePackageValidator(
          platform: ReleasePackagePlatform.windows,
          zipPath: p.join(temp.path, 'unused.zip'),
          requireWindowsSignature: true,
          requireWindowsUnsigned: true,
        ).validate(),
        throwsA(isA<StateError>()),
      );
      await expectLater(
        () => ReleasePackageValidator(
          platform: ReleasePackagePlatform.linux,
          zipPath: p.join(temp.path, 'unused.zip'),
          requireWindowsUnsigned: true,
        ).validate(),
        throwsA(isA<StateError>()),
      );
      await expectLater(
        () => ReleasePackageValidator(
          platform: ReleasePackagePlatform.windows,
          zipPath: p.join(temp.path, 'unused.zip'),
          requireWindowsUnsigned: true,
          expectedWindowsSignerSha256: List.filled(64, 'a').join(),
        ).validate(),
        throwsA(isA<StateError>()),
      );
    },
  );

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
