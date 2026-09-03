import 'package:launcher_domain/launcher_domain.dart';
import 'package:test/test.dart';

void main() {
  group('manifest multiplayer contract', () {
    test('uses optional multiplayer metadata in schema V5', () {
      final standalone = ModManifest.fromJson(_manifest());
      expect(_blocking(standalone), isEmpty);
      expect(standalone.multiplayer, isNull);

      final declared = ModManifest.fromJson({
        ..._manifest(),
        'multiplayer': {'mode': 'client-local'},
      });
      expect(_blocking(declared), isEmpty);
    });

    test('rejects a present non-object multiplayer value', () {
      for (final value in <Object?>[null, 'session']) {
        final manifest = ModManifest.fromJson({
          ..._manifest(),
          'multiplayer': value,
        });

        expect(_messages(manifest), contains('multiplayer must be an object'));
      }
    });

    test(
      'accepts client-local and server-only modes without session fields',
      () {
        for (final mode in const ['client-local', 'server-only']) {
          final manifest = ModManifest.fromJson({
            ..._manifest(),
            'schemaVersion': ModManifest.currentSchemaVersion,
            'multiplayer': {'mode': mode},
          });

          expect(_blocking(manifest), isEmpty, reason: mode);
          expect(manifest.multiplayer?.modeName, mode);
          expect(manifest.toJson()['multiplayer'], {'mode': mode});
        }
      },
    );

    test('rejects session-only fields on non-session modes', () {
      final manifest = ModManifest.fromJson({
        ..._manifest(),
        'schemaVersion': ModManifest.currentSchemaVersion,
        'multiplayer': {
          'mode': 'client-local',
          'presence': 'optional',
          'protocol': {'version': '1.0.0'},
          'synchronizedFiles': <Object?>[],
        },
      });

      expect(_messages(manifest), contains('only valid for session mode'));
    });

    test('round-trips a complete session declaration', () {
      final manifest = ModManifest.fromJson({
        ..._manifest(),
        'schemaVersion': ModManifest.currentSchemaVersion,
        'multiplayer': {
          'mode': 'session',
          'presence': 'required',
          'protocol': {
            'version': '1.0.0',
            'peerVersionRange': '>=1.0.0 <2.0.0',
          },
          'synchronizedFiles': ['Content/gameplay-rules.json'],
        },
      });

      expect(_blocking(manifest), isEmpty);
      expect(manifest.multiplayer?.mode, ModMultiplayerMode.session);
      expect(manifest.multiplayer?.presence, ModMultiplayerPresence.required);
      expect(manifest.toJson()['multiplayer'], {
        'mode': 'session',
        'presence': 'required',
        'protocol': {'version': '1.0.0', 'peerVersionRange': '>=1.0.0 <2.0.0'},
        'synchronizedFiles': ['Content/gameplay-rules.json'],
      });
    });

    test('allows exact protocol matching by omitting the peer range', () {
      final manifest = ModManifest.fromJson({
        ..._manifest(),
        'schemaVersion': ModManifest.currentSchemaVersion,
        'multiplayer': {
          'mode': 'session',
          'presence': 'optional',
          'protocol': {'version': '1.2.3'},
        },
      });

      expect(_blocking(manifest), isEmpty);
      expect(
        manifest.multiplayer?.protocol?.peerVersionRangeIsPresent,
        isFalse,
      );
    });

    test('rejects malformed protocols and synchronized paths', () {
      final manifest = ModManifest.fromJson({
        ..._manifest(),
        'schemaVersion': ModManifest.currentSchemaVersion,
        'multiplayer': {
          'mode': 'session',
          'presence': 'sometimes',
          'protocol': {'version': '1.0', 'peerVersionRange': 'not a range'},
          'synchronizedFiles': [
            '../outside.json',
            'Content/config.json',
            'content/CONFIG.json',
          ],
        },
      });

      final messages = _messages(manifest);
      expect(messages, contains('presence to be required or optional'));
      expect(messages, contains('exact semantic version'));
      expect(messages, contains('must be a valid version range'));
      expect(messages, contains('safe portable relative path'));
      expect(messages, contains('portable-collision'));
    });

    test('rejects generated package metadata as synchronized content', () {
      for (final path in const [
        'topiaforge.mod.json',
        'TOPIAFORGE.INSTALL.JSON',
      ]) {
        final manifest = ModManifest.fromJson({
          ..._manifest(),
          'multiplayer': {
            'mode': 'session',
            'presence': 'required',
            'protocol': {'version': '1.0.0'},
            'synchronizedFiles': [path],
          },
        });

        expect(
          _messages(manifest),
          contains('cannot include generated package metadata'),
          reason: path,
        );
      }
    });
  });
}

Map<String, Object?> _manifest() => {
  'schemaVersion': ModManifest.currentSchemaVersion,
  'name': 'sample.multiplayer',
  'displayName': 'Sample Multiplayer',
  'version': '1.0.0',
  'author': {'name': 'Tester'},
  'entryAssembly': 'Sample.Multiplayer.dll',
  'entryType': 'Sample.Multiplayer.Mod',
  'supportedGameVersionRange': '*',
  'supportedLoaderVersionRange': '*',
  'supportedSdkVersionRange': '*',
};

Iterable<LauncherIssue> _blocking(ModManifest manifest) =>
    manifest.validate().where((issue) => issue.isBlocking);

String _messages(ModManifest manifest) =>
    manifest.validate().map((issue) => issue.message).join(' ');
