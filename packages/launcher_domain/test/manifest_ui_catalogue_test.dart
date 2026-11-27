import 'package:launcher_domain/launcher_domain.dart';
import 'package:test/test.dart';

void main() {
  test('the optional UI catalogue round-trips without changing V6', () {
    final plain = ModManifest.fromJson(_manifest());
    expect(plain.ui, isNull);
    expect(plain.toJson().containsKey('ui'), isFalse);

    final ui = _catalogue(hash: 'a' * 64);
    final manifest = ModManifest.fromJson({..._manifest(), 'ui': ui});
    expect(manifest.validate().where((issue) => issue.isBlocking), isEmpty);
    final catalogue = manifest.ui!;
    expect(catalogue.defaultThemeId, 'survival');
    expect(catalogue.themes.single.id, 'survival');
    expect(catalogue.themes.single.definition, 'ui/themes/survival.json');
    expect(catalogue.themes.single.sha256, 'a' * 64);
    expect(manifest.toJson()['ui'], ui);
    expect(ModManifest.fromJson(manifest.toJson()).toJson()['ui'], ui);
    expect(() => catalogue.themes.clear(), throwsUnsupportedError);
  });

  for (final (name, ui, diagnostic) in [
    ('null catalogue', null, 'ui must be an object'),
    ('unknown version', {..._catalogue(), 'schema': 2}, 'ui.schema'),
    (
      'undeclared default',
      {..._catalogue(), 'default': 'missing'},
      'ui.default',
    ),
    ('explicit null default', {..._catalogue(), 'default': null}, 'ui.default'),
    ('unknown field', {..._catalogue(), 'palette': 'dark'}, 'unknown field'),
    (
      'too many themes',
      {..._catalogue(), 'themes': List.filled(9, _theme())},
      'eight entries',
    ),
    (
      'duplicate theme',
      {
        ..._catalogue(),
        'themes': [_theme(), _theme()],
      },
      'unique',
    ),
    (
      'escaped path',
      {
        ..._catalogue(),
        'themes': [_theme(path: '../theme.json')],
      },
      'portable relative path',
    ),
    (
      'long UTF-8 path',
      {
        ..._catalogue(),
        'themes': [_theme(path: '${'é' * 128}.json')],
      },
      '256 UTF-8 bytes',
    ),
    ('invalid hash', _catalogue(hash: 'broken'), 'SHA-256'),
  ]) {
    test('rejects $name', () {
      final manifest = ModManifest.fromJson({..._manifest(), 'ui': ui});
      expect(
        manifest
            .validate()
            .where((issue) => issue.isBlocking)
            .map((issue) => issue.message),
        contains(contains(diagnostic)),
      );
    });
  }
}

Map<String, Object?> _manifest() => {
  'schemaVersion': 6,
  'name': 'test.themed',
  'displayName': 'Themed package',
  'version': '1.0.0',
  'author': {'name': 'Example Author'},
  'entryAssembly': 'Example.dll',
  'entryType': 'Example.Mod',
  'supportedGameVersionRange': '*',
  'supportedLoaderVersionRange': '>=0.2.0',
  'supportedSdkVersionRange': '>=0.2.0',
};

Map<String, Object?> _catalogue({String? hash}) => {
  'schema': 1,
  'default': 'survival',
  'themes': [_theme(hash: hash)],
};

Map<String, Object?> _theme({
  String path = 'ui/themes/survival.json',
  String? hash,
}) => {'id': 'survival', 'definition': path, 'sha256': ?hash};
