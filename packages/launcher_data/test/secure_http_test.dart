import 'package:launcher_data/src/secure_http.dart';
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
}
