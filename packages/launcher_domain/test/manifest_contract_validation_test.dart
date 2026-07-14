import 'package:launcher_domain/launcher_domain.dart';
import 'package:test/test.dart';

void main() {
  group('ModManifest contract validation', () {
    test(
      'preserves additive unknown fields through canonical serialization',
      () {
        final manifest = ModManifest.fromJson({
          'schemaVersion': 2,
          'name': 'sample.forward-compatible',
          'displayName': 'Forward Compatible',
          'version': '1.2.3',
          'author': {'name': 'Test'},
          'entryAssembly': 'ForwardCompatible.dll',
          'entryType': 'Sample.ForwardCompatible.Mod',
          'futureObject': {
            'enabled': true,
            'modes': ['one', 'two'],
          },
        });

        expect(manifest.extraFields.keys, ['futureObject']);
        expect(manifest.toJson()['futureObject'], {
          'enabled': true,
          'modes': ['one', 'two'],
        });
      },
    );

    test('parses but rejects a bare-string author', () {
      final manifest = ModManifest.fromJson(
        _manifestJson(author: 'QuantumWorks'),
      );

      expect(manifest.author.name, 'QuantumWorks');
      expect(manifest.authorIsObject, isFalse);
      expect(
        manifest.validate().where((issue) => issue.isBlocking),
        contains(
          isA<LauncherIssue>().having(
            (issue) => issue.message,
            'message',
            contains('author must be an object'),
          ),
        ),
      );
    });

    test('accepts an object-shaped author', () {
      final manifest = ModManifest.fromJson(
        _manifestJson(author: {'name': 'QuantumWorks'}),
      );

      expect(manifest.authorIsObject, isTrue);
      expect(manifest.validate().where((issue) => issue.isBlocking), isEmpty);
    });

    test('accepts a complete SemVer 2.0.0 version', () {
      final manifest = ModManifest.fromJson(
        _manifestJson(version: '1.2.3-rc.1+build.001'),
      );

      expect(manifest.validate().where((issue) => issue.isBlocking), isEmpty);
    });

    test('rejects versions outside the SemVer 2.0.0 contract', () {
      for (final version in [
        '1',
        '1.0',
        '01.0.0',
        '1.0.0-preview.01',
        '1.0.0+build_tag',
      ]) {
        final manifest = ModManifest.fromJson(_manifestJson(version: version));

        expect(
          manifest.validate().where((issue) => issue.isBlocking),
          contains(
            isA<LauncherIssue>().having(
              (issue) => issue.message,
              'message',
              contains('SemVer 2.0.0'),
            ),
          ),
          reason: version,
        );
      }
    });

    for (final unsafePath in [
      '/absolute.dll',
      'C:drive-relative.dll',
      'C:/absolute.dll',
      'payload.dll:stream',
      'folder//file.dll',
      'folder/./file.dll',
      'folder/../file.dll',
      'NUL.txt',
      'folder/aux.dll',
      'folder/trailing.',
      'folder/trailing ',
      'folder/\u0001.dll',
    ]) {
      test('rejects non-portable entryAssembly path $unsafePath', () {
        final manifest = ModManifest.fromJson(
          _manifestJson(entryAssembly: unsafePath),
        );

        expect(
          manifest.validate().where((issue) => issue.isBlocking),
          contains(
            isA<LauncherIssue>().having(
              (issue) => issue.message,
              'message',
              contains('entryAssembly must be a relative file path'),
            ),
          ),
        );
      });
    }

    test('applies portable-path rules to API assemblies', () {
      final manifest = ModManifest.fromJson({
        ..._manifestJson(),
        'apiAssemblies': ['refs/COM1.dll'],
      });

      expect(
        manifest.validate().where((issue) => issue.isBlocking),
        contains(
          isA<LauncherIssue>().having(
            (issue) => issue.message,
            'message',
            contains('apiAssemblies entries must be safe'),
          ),
        ),
      );
    });

    test('accepts portable nested package paths', () {
      final manifest = ModManifest.fromJson({
        ..._manifestJson(entryAssembly: r'bin\Validation.dll'),
        'apiAssemblies': ['refs/Validation.Api.dll'],
      });

      expect(manifest.validate().where((issue) => issue.isBlocking), isEmpty);
    });

    test('round-trips typed license files and rejects portable collisions', () {
      final valid = ModManifest.fromJson({
        ..._manifestJson(),
        'licenseFiles': ['licenses/LICENSE.txt', 'NOTICE.md'],
      });
      expect(valid.licenseFiles, ['licenses/LICENSE.txt', 'NOTICE.md']);
      expect(valid.toJson()['licenseFiles'], valid.licenseFiles);
      expect(valid.extraFields, isNot(contains('licenseFiles')));

      final invalid = ModManifest.fromJson({
        ..._manifestJson(),
        'licenseFiles': [
          'licenses/fullwidth-A.txt',
          'licenses/fullwidth-Ａ.txt',
          r'licenses\escape.txt',
        ],
      });
      final messages = invalid
          .validate()
          .map((issue) => issue.message)
          .join(' ');
      expect(messages, contains('portable-collision'));
      expect(messages, contains('safe portable relative path'));
    });
  });
}

Map<String, Object?> _manifestJson({
  Object author = const {'name': 'QuantumWorks'},
  String entryAssembly = 'Validation.dll',
  String version = '1.0.0',
}) => {
  'schemaVersion': 2,
  'name': 'validation.mod',
  'displayName': 'Validation Mod',
  'version': version,
  'author': author,
  'entryAssembly': entryAssembly,
  'entryType': 'Validation.Entry',
};
