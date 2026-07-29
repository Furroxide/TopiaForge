import 'package:launcher_domain/launcher_domain.dart';
import 'package:test/test.dart';

void main() {
  group('manifest schema dispatch', () {
    test('rejects retired V4 before interpreting V5 multiplayer fields', () {
      expect(
        () => ModManifest.fromJson({
          ..._manifest(),
          'schemaVersion': 4,
          'multiplayer': {'mode': Object(), 'protocol': Object()},
        }),
        throwsA(
          isA<FormatException>()
              .having(
                (error) => error.message,
                'message',
                contains('retired before TopiaForge 1.0'),
              )
              .having(
                (error) => error.message,
                'message',
                contains('topiaforge migrate-manifest'),
              )
              .having(
                (error) => error.message,
                'message',
                contains('schemaVersion 5'),
              ),
        ),
      );
    });

    test('rejects unsupported old and future schemas before V5 fields', () {
      for (final schemaVersion in const [3, 6]) {
        expect(
          () => ModManifest.fromJson({
            'schemaVersion': schemaVersion,
            'displayName': Object(),
            'multiplayer': Object(),
          }),
          throwsA(
            isA<FormatException>().having(
              (error) => error.message,
              'message',
              allOf(
                contains('Unsupported'),
                contains('schemaVersion $schemaVersion'),
              ),
            ),
          ),
          reason: 'schemaVersion $schemaVersion',
        );
      }
    });

    test('validator does not reinterpret a future schema through V5', () {
      final manifest = ModManifest(
        schemaVersion: 6,
        id: '',
        name: '',
        version: '',
        multiplayer: const ModMultiplayerMetadata(modeName: 'future-mode'),
      );

      final issues = manifest.validate();
      expect(issues, hasLength(1));
      expect(issues.single.message, contains('schemaVersion must be 5'));
    });

    test('rejects missing and non-integer schema selectors', () {
      for (final manifest in <Map<String, Object?>>[
        {..._manifest()}..remove('schemaVersion'),
        {..._manifest(), 'schemaVersion': 5.0},
        {..._manifest(), 'schemaVersion': '5'},
      ]) {
        expect(
          () => ModManifest.fromJson(manifest),
          throwsA(isA<FormatException>()),
        );
      }
    });

    test('dispatches V5 to the multiplayer decoder', () {
      final manifest = ModManifest.fromJson({
        ..._manifest(),
        'multiplayer': {
          'mode': 'session',
          'presence': 'required',
          'protocol': {'version': '1.2.3'},
          'synchronizedFiles': ['Content/rules.json'],
        },
      });

      expect(manifest.schemaVersion, 5);
      expect(manifest.multiplayer?.mode, ModMultiplayerMode.session);
      expect(manifest.multiplayer?.protocol?.version, '1.2.3');
    });
  });
}

Map<String, Object?> _manifest() => {
  'schemaVersion': 5,
  'name': 'sample.schema-dispatch',
  'displayName': 'Schema Dispatch',
  'version': '1.0.0',
  'author': {'name': 'Tester'},
  'entryAssembly': 'Sample.SchemaDispatch.dll',
  'entryType': 'Sample.SchemaDispatch.Mod',
  'supportedGameVersionRange': '*',
  'supportedLoaderVersionRange': '*',
  'supportedSdkVersionRange': '*',
};
