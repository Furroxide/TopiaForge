import 'package:launcher_domain/launcher_domain.dart';
import 'package:test/test.dart';

void main() {
  group('ModManifest contract validation', () {
    test(
      'preserves namespaced extension fields through canonical serialization',
      () {
        final manifest = ModManifest.fromJson({
          'schemaVersion': ModManifest.currentSchemaVersion,
          'name': 'sample.forward-compatible',
          'displayName': 'Forward Compatible',
          'version': '1.2.3',
          'author': {'name': 'Test'},
          'entryAssembly': 'ForwardCompatible.dll',
          'entryType': 'Sample.ForwardCompatible.Mod',
          'x-future-object': {
            'enabled': true,
            'modes': ['one', 'two'],
          },
        });

        expect(manifest.extraFields.keys, ['x-future-object']);
        expect(manifest.toJson()['x-future-object'], {
          'enabled': true,
          'modes': ['one', 'two'],
        });
      },
    );

    test('parses but rejects a bare-string author', () {
      final manifest = ModManifest.fromJson(
        _manifestJson(author: 'TopiaForge'),
      );

      expect(manifest.author.name, 'TopiaForge');
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
        _manifestJson(author: {'name': 'TopiaForge'}),
      );

      expect(manifest.authorIsObject, isTrue);
      expect(manifest.validate().where((issue) => issue.isBlocking), isEmpty);
    });

    test('rejects retired manifest field aliases', () {
      for (final entry in const <String, Object?>{
        'id': 'alias.mod',
        'title': 'Alias Mod',
        'gameVersion': '1.0.0',
        'gameVersionRange': '>=1.0.0',
        'loaderVersionRange': '>=1.0.0',
        'sdkVersionRange': '>=1.0.0',
        'packageHashes': <String, String>{'sha256': 'abc'},
        'gamemodes': <Object?>[],
        'legacyFolders': <String, String>{},
        'legacyFiles': <String, String>{},
        'legacyPackages': <Object?>[],
      }.entries) {
        final manifest = ModManifest.fromJson({
          ..._manifestJson(),
          entry.key: entry.value,
        });

        expect(
          manifest.validate().where((issue) => issue.isBlocking),
          isNotEmpty,
          reason: entry.key,
        );
      }
    });

    test('rejects retired dependency and conflict version aliases', () {
      expect(
        () => ModManifest.fromJson({
          ..._manifestJson(),
          'dependencies': [
            {'id': 'dependency.mod', 'version': '1.0.0'},
          ],
        }),
        throwsFormatException,
      );
      expect(
        () => ModManifest.fromJson({
          ..._manifestJson(),
          'conflicts': [
            {'id': 'conflict.mod', 'version': '1.0.0'},
          ],
        }),
        throwsFormatException,
      );
    });

    test('rejects the retired ai permission', () {
      final manifest = ModManifest.fromJson({
        ..._manifestJson(),
        'capabilities': ['ai'],
      });

      expect(
        manifest.validate().where((issue) => issue.isBlocking),
        isNotEmpty,
      );
    });

    test('rejects retired ecosystem ID namespaces', () {
      for (final id in const [
        'robo'
            'topia.example',
        'com.robo'
            'topia.example',
        'quantum'
            'works.example',
      ]) {
        final manifest = ModManifest.fromJson({..._manifestJson(), 'name': id});

        expect(
          manifest.validate().where((issue) => issue.isBlocking),
          isNotEmpty,
          reason: id,
        );
      }
    });

    test('names the retired world gamemode list rather than the key', () {
      // Decoded only so the error can say what the field was. Falling through
      // to the unknown-field check would report a typo, which is not what
      // happened and does not say where those entries went.
      final manifest = ModManifest.fromJson({
        ..._manifestJson(),
        'worldGamemodes': [
          {'id': 'author.mode.old', 'name': 'Old Mode'},
        ],
      });

      final blocking = manifest.validate().where((issue) => issue.isBlocking);

      expect(
        blocking.map((issue) => issue.message),
        contains(contains('worldGamemodes is not supported')),
      );
      expect(
        blocking.map((issue) => issue.message),
        isNot(contains(contains('extensions must use x-*'))),
        reason: 'a retired field is not an unknown one',
      );
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
        ..._manifestJson(entryAssembly: 'bin/Validation.dll'),
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
  Object author = const {'name': 'TopiaForge'},
  String entryAssembly = 'Validation.dll',
  String version = '1.0.0',
}) => {
  'schemaVersion': ModManifest.currentSchemaVersion,
  'name': 'validation.mod',
  'displayName': 'Validation Mod',
  'version': version,
  'author': author,
  'entryAssembly': entryAssembly,
  'entryType': 'Validation.Entry',
  'supportedGameVersionRange': '*',
  'supportedLoaderVersionRange': '*',
  'supportedSdkVersionRange': '*',
};
