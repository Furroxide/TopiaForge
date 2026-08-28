part of 'release_package_builder_test.dart';

/// Windows Authenticode cases: signing, reviewed-signer verification, and the
/// unsigned dry-run. Split out of the process/IO cases when those crossed the
/// 500-line cap; these share a subject rather than merely a file.
void _registerReleaseWindowsSigningTests() {
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
    'Windows unsigned verification treats a lone timestamp as conclusive',
    () async {
      // A timestamper certificate without a signer is odd, but for a check
      // that demands an entirely unsigned package any certificate settles the
      // question. Retrying this as though it were an unreadable file would
      // eventually report "could not determine" for a file that told us.
      final stage = Directory(p.join(temp.path, 'windows-stamped-stage'))
        ..createSync();
      for (final relative in [
        'topiaforge.exe',
        'TopiaForge.GameCompat.Extractor.exe',
        p.join('launcher', 'topiaforge_launcher.exe'),
      ]) {
        _writeFile(stage, p.split(relative), 'stamped executable fixture');
      }
      final runner = _RecordingProcessRunner(
        onResult: (_) => ProcessResult(1, 0, 'UnknownError|none|stamp', ''),
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
            contains('with a timestamp'),
          ),
        ),
      );
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
}
