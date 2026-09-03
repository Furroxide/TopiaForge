import 'package:launcher_domain/launcher_domain.dart';
import 'package:test/test.dart';

void main() {
  group('manifest schema dispatch', () {
    test('rejects a retired V5 with the command that moves it forward', () {
      expect(
        () => ModManifest.fromJson({..._manifest(), 'schemaVersion': 5}),
        throwsA(
          isA<FormatException>()
              .having((error) => error.message, 'message', contains('retired'))
              .having(
                (error) => error.message,
                'message',
                contains('migrate-manifest'),
              )
              .having(
                (error) => error.message,
                'message',
                contains('worldGamemodes'),
              ),
        ),
        reason:
            'an author whose manifest stopped loading needs the next command, '
            'not a verdict',
      );
    });

    test('rejects retired V4 before interpreting later multiplayer fields', () {
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
      for (final schemaVersion in const [3, 7]) {
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
        schemaVersion: 7,
        id: '',
        name: '',
        version: '',
        multiplayer: const ModMultiplayerMetadata(modeName: 'future-mode'),
      );

      final issues = manifest.validate();
      expect(issues, hasLength(1));
      expect(issues.single.message, contains('schemaVersion must be 6'));
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

    test('dispatches V6 to the contribution decoder', () {
      final manifest = ModManifest.fromJson({
        ..._manifest(),
        'schemaVersion': 6,
        'capabilities': ['world-service'],
        'contributions': {
          'gamemodes': [
            {
              'id': 'sample.schema-dispatch.mode',
              'name': 'Dispatch Mode',
              'implementation': {'type': 'Sample.SchemaDispatch.Mode'},
            },
          ],
        },
      });

      expect(manifest.schemaVersion, 6);
      expect(
        manifest.contributions?.gamemodes.single.implementation?.type,
        'Sample.SchemaDispatch.Mode',
      );
    });

    test('the retired list is named, not reported as an unknown field', () {
      // Decoded only so the error can say where those entries went. Falling
      // through to the closed-field check would report an unknown field, which
      // tells the author nothing.
      final issues = ModManifest.fromJson({
        ..._manifest(),
        'worldGamemodes': [
          {'id': 'sample.schema-dispatch.mode', 'name': 'Dispatch Mode'},
        ],
      }).validate();

      expect(
        issues.map((issue) => issue.message),
        contains(contains('worldGamemodes is not supported')),
      );
    });

    test('dispatches to the multiplayer decoder', () {
      final manifest = ModManifest.fromJson({
        ..._manifest(),
        'multiplayer': {
          'mode': 'session',
          'presence': 'required',
          'protocol': {'version': '1.2.3'},
          'synchronizedFiles': ['Content/rules.json'],
        },
      });

      expect(manifest.schemaVersion, ModManifest.currentSchemaVersion);
      expect(manifest.multiplayer?.mode, ModMultiplayerMode.session);
      expect(manifest.multiplayer?.protocol?.version, '1.2.3');
    });
  });
}

Map<String, Object?> _manifest() => {
  'schemaVersion': ModManifest.currentSchemaVersion,
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
