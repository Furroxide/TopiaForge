import 'dart:convert';
import 'dart:io';

import 'package:archive/archive.dart';
import 'package:crypto/crypto.dart';
import 'package:launcher_data/launcher_data.dart';
import 'package:path/path.dart' as p;
import 'package:test/test.dart';

const _definition = 'ui/themes/survival.json';
const _definitionJson = '{\n  "schema": 1,\n  "id": "survival"\n}\n';

void main() {
  late Directory root;
  late Directory project;
  late Directory output;

  setUp(() {
    root = Directory.systemTemp.createTempSync('topiaforge-ui-pack-test-');
    project = Directory(p.join(root.path, 'project'))..createSync();
    output = Directory(p.join(root.path, 'output'));
    File(
      p.join(project.path, 'Example.dll'),
    ).writeAsStringSync('packing fixture');
  });

  tearDown(() => root.deleteSync(recursive: true));

  test(
    'pack writes an exact definition hash and validates those staged bytes',
    () async {
      var calls = 0;
      final bytes = utf8.encode(_definitionJson);
      final digest = sha256.convert(bytes).toString();
      _writeDefinition(project, bytes);
      final original = _writeManifest(project);
      final repository = LocalDeveloperRepository(
        repositoryRoot: root.path,
        dataRoot: p.join(root.path, 'data'),
        uiThemePackageValidator: (staged) async {
          calls++;
          expect(
            File(p.join(staged.path, _definition)).readAsBytesSync(),
            bytes,
          );
          expect(_stagedTheme(_readManifest(staged))['sha256'], digest);
          return [];
        },
      );

      final archive = await repository.packModDirectory(
        project.path,
        outputDir: output.path,
      );
      expect(calls, 1);
      expect(
        File(p.join(project.path, 'topiaforge.mod.json')).readAsStringSync(),
        original,
      );
      expect(File(p.join(project.path, _definition)).readAsBytesSync(), bytes);

      final decoded = ZipDecoder().decodeBytes(File(archive).readAsBytesSync());
      expect(
        decoded.files.where((file) => file.name == _definition),
        hasLength(1),
      );
      expect(decoded.findFile(_definition)!.content, bytes);
      final manifestBytes =
          decoded.findFile('topiaforge.mod.json')!.content as List<int>;
      expect(
        _stagedTheme(jsonDecode(utf8.decode(manifestBytes)) as Map)['sha256'],
        digest,
      );

      final checked = await repository.checkPackage(archive);
      expect(checked.ui?.defaultThemeId, 'survival');
      expect(checked.ui?.themes.single.sha256, digest);
      expect(calls, 2);

      await repository.checkPackage(project.path);
      expect(calls, 3);
    },
  );

  test(
    'pack refuses an asserted hash that disagrees with source bytes',
    () async {
      _writeDefinition(project, utf8.encode(_definitionJson));
      _writeManifest(project, hash: '0' * 64);
      final repository = LocalDeveloperRepository(
        repositoryRoot: root.path,
        dataRoot: p.join(root.path, 'data'),
        uiThemePackageValidator: (_) async =>
            fail('stale bytes cannot reach the validator'),
      );
      await expectLater(
        repository.packModDirectory(project.path, outputDir: output.path),
        throwsA(
          isA<StateError>().having(
            (e) => e.message,
            'reason',
            contains('ui.themes[0].sha256'),
          ),
        ),
      );
      expect(output.existsSync(), isFalse);
    },
  );

  test(
    'pack refuses an oversized definition before staging validation',
    () async {
      _writeDefinition(project, List.filled(64 * 1024 + 1, 32));
      _writeManifest(project);
      final repository = LocalDeveloperRepository(
        repositoryRoot: root.path,
        dataRoot: p.join(root.path, 'data'),
        uiThemePackageValidator: (_) async =>
            fail('excess bytes cannot reach the validator'),
      );
      await expectLater(
        repository.packModDirectory(project.path, outputDir: output.path),
        throwsA(
          isA<StateError>().having(
            (e) => e.message,
            'reason',
            contains('65536-byte limit'),
          ),
        ),
      );
      expect(output.existsSync(), isFalse);
    },
  );

  test(
    'pack refuses catalogue definitions behind a linked directory',
    () async {
      final outside = Directory(p.join(root.path, 'outside'))..createSync();
      _writeDefinition(outside, utf8.encode(_definitionJson));
      try {
        Link(p.join(project.path, 'ui')).createSync(p.join(outside.path, 'ui'));
      } on FileSystemException {
        if (Platform.isWindows) {
          markTestSkipped('Windows account cannot create symbolic links.');
          return;
        }
        rethrow;
      }
      _writeManifest(project);
      final repository = LocalDeveloperRepository(
        repositoryRoot: root.path,
        dataRoot: p.join(root.path, 'data'),
        uiThemePackageValidator: (_) async =>
            fail('external bytes cannot reach the validator'),
      );
      await expectLater(
        repository.packModDirectory(project.path, outputDir: output.path),
        throwsA(
          isA<StateError>().having(
            (e) => e.message,
            'reason',
            contains('ui.themes[0].definition'),
          ),
        ),
      );
      expect(output.existsSync(), isFalse);
    },
  );

  test('pack refuses a theme rejected by the managed validator', () async {
    _writeDefinition(project, utf8.encode(_definitionJson));
    _writeManifest(project);
    final repository = LocalDeveloperRepository(
      repositoryRoot: root.path,
      dataRoot: p.join(root.path, 'data'),
      uiThemePackageValidator: (_) async => [
        'paper.colors.primary fails contrast.',
      ],
    );
    await expectLater(
      repository.packModDirectory(project.path, outputDir: output.path),
      throwsA(
        isA<StateError>().having(
          (e) => e.message,
          'reason',
          contains('fails contrast'),
        ),
      ),
    );
    expect(output.existsSync(), isFalse);
  });

  test('pack without a UI catalogue uses the original path', () async {
    _writeManifest(project, includeUi: false);
    final repository = LocalDeveloperRepository(
      repositoryRoot: root.path,
      dataRoot: p.join(root.path, 'data'),
      uiThemePackageValidator: (_) async =>
          fail('unthemed mods need no UI validator'),
    );
    final path = await repository.packModDirectory(
      project.path,
      outputDir: output.path,
    );
    expect((await repository.checkPackage(path)).ui, isNull);
  });
}

void _writeDefinition(Directory package, List<int> bytes) {
  File(p.join(package.path, _definition))
    ..createSync(recursive: true)
    ..writeAsBytesSync(bytes);
}

String _writeManifest(
  Directory package, {
  String? hash,
  bool includeUi = true,
}) {
  final json = jsonEncode({
    'schemaVersion': 6,
    'name': 'example.themed',
    'displayName': 'Themed package',
    'version': '1.0.0',
    'author': {'name': 'Example Author'},
    'entryAssembly': 'Example.dll',
    'entryType': 'Example.Mod',
    'supportedGameVersionRange': '*',
    'supportedLoaderVersionRange': '>=0.2.0',
    'supportedSdkVersionRange': '>=0.2.0',
    if (includeUi)
      'ui': {
        'schema': 1,
        'default': 'survival',
        'themes': [
          {'id': 'survival', 'definition': _definition, 'sha256': ?hash},
        ],
      },
  });
  File(p.join(package.path, 'topiaforge.mod.json')).writeAsStringSync(json);
  return json;
}

Map _readManifest(Directory root) =>
    jsonDecode(
          File(p.join(root.path, 'topiaforge.mod.json')).readAsStringSync(),
        )
        as Map;

Map _stagedTheme(Map manifest) =>
    (manifest['ui']['themes'] as List).single as Map;
