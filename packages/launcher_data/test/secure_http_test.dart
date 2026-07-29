import 'dart:io';

import 'package:launcher_data/src/secure_http.dart';
import 'package:launcher_data/launcher_data.dart';
import 'package:test/test.dart';

void main() {
  test('secure fetch rejects plaintext and credential-bearing URLs', () async {
    await expectLater(
      fetchHttpsBytes(
        Uri.parse('http://packages.example/mod.zip'),
        maxBytes: 1024,
        label: 'Package',
      ),
      throwsA(
        isA<StateError>().having(
          (error) => error.message,
          'message',
          contains('HTTPS'),
        ),
      ),
    );
    await expectLater(
      fetchHttpsBytes(
        Uri.parse('https://user:secret@packages.example/mod.zip'),
        maxBytes: 1024,
        label: 'Package',
      ),
      throwsA(
        isA<StateError>().having(
          (error) => error.message,
          'message',
          contains('credentials'),
        ),
      ),
    );
    await expectLater(
      fetchHttpsBytes(
        Uri.parse('https://packages.example/${List.filled(5000, 'a').join()}'),
        maxBytes: 1024,
        label: 'Package',
      ),
      throwsStateError,
    );
    for (final url in [
      'https://packages.example/mod.zip?token=secret',
      'https://packages.example/mod.zip#latest',
    ]) {
      await expectLater(
        fetchHttpsBytes(Uri.parse(url), maxBytes: 1024, label: 'Package'),
        throwsA(
          isA<StateError>().having(
            (error) => error.message,
            'message',
            contains('query or fragment'),
          ),
        ),
      );
    }
  });

  test('secure fetch rejects invalid bounds before opening a socket', () async {
    await expectLater(
      fetchHttpsBytes(
        Uri.parse('https://packages.example/mod.zip'),
        maxBytes: -1,
        label: 'Package',
      ),
      throwsArgumentError,
    );
  });

  test('update transport rejects mutable or non-GitHub endpoints', () async {
    final transport = SecureLauncherUpdateTransport();
    addTearDown(transport.close);
    for (final uri in [
      Uri.parse(
        'https://github.com/furroxide/TopiaForge/releases/download/'
        'v1.0.0/file.zip?token=secret',
      ),
      Uri.parse('https://release-assets.githubusercontent.com/file?token=x'),
      Uri.parse(
        'https://api.github.com/repos/furroxide/TopiaForge/releases?per_page=100',
      ),
      Uri.parse('https://example.com/update.zip'),
    ]) {
      await expectLater(
        transport.fetch(uri, maxBytes: 1024, label: 'Update'),
        throwsStateError,
      );
    }
  });

  test(
    'update downloads reject invalid bounds before creating partials',
    () async {
      final transport = SecureLauncherUpdateTransport();
      addTearDown(transport.close);
      await expectLater(
        transport.download(
          Uri.parse(
            'https://github.com/furroxide/TopiaForge/releases/download/'
            'v1.0.0/file.zip',
          ),
          partialFile: File('unused.partial'),
          expectedSize: 0,
          expectedSha256: 'invalid',
        ),
        throwsStateError,
      );
      expect(File('unused.partial').existsSync(), isFalse);
    },
  );
}
