import 'dart:convert';
import 'dart:io';

import 'package:json_schema/json_schema.dart';
import 'package:launcher_domain/launcher_domain.dart';
import 'package:test/test.dart';

void main() {
  test('versioned V5 schema is frozen and self-contained', () {
    final root = _repoRoot();
    Map<String, Object?> readSchema(String name) =>
        jsonDecode(File(_join(root.path, ['schemas', name])).readAsStringSync())
            as Map<String, Object?>;

    final latest = readSchema('topiaforge.mod.schema.json');
    final versioned = readSchema('topiaforge.mod.v5.schema.json');
    expect(
      jsonEncode(versioned),
      isNot(contains('/schemas/topiaforge.mod.schema.json')),
      reason:
          'a frozen versioned schema must never reference the mutable latest schema',
    );
    expect(versioned['properties'], isA<Map>());
    expect(
      ((versioned['properties'] as Map)['schemaVersion'] as Map)['const'],
      ModManifest.currentSchemaVersion,
    );

    for (final schema in [latest, versioned]) {
      schema.remove(r'$id');
      schema.remove('title');
      schema.remove('description');
    }
    expect(
      versioned,
      latest,
      reason:
          'while V5 is latest, its frozen schema and editor alias must remain semantically identical',
    );
  });

  test('checked-in manifests satisfy schema V5', () {
    final root = _repoRoot();
    final schemaJson =
        jsonDecode(
              File(
                _join(root.path, ['schemas', 'topiaforge.mod.schema.json']),
              ).readAsStringSync(),
            )
            as Map<String, Object?>;
    final schema = JsonSchema.create(schemaJson);
    final manifestFiles = [
      ...Directory(_join(root.path, ['mods']))
          .listSync(recursive: true)
          .whereType<File>()
          .where((file) => file.path.endsWith('topiaforge.mod.json')),
    ];

    expect(manifestFiles, isNotEmpty);
    for (final file in manifestFiles) {
      final json = jsonDecode(file.readAsStringSync()) as Map<String, Object?>;
      final result = schema.validate(json);
      expect(
        result.isValid,
        isTrue,
        reason: '${file.path}\n${result.errors.join('\n')}',
      );
      final blocking = ModManifest.fromJson(
        json,
      ).validate().where((issue) => issue.isBlocking);
      expect(blocking, isEmpty, reason: file.path);
    }
  });

  test('schema and domain reject the same unsafe entry assembly paths', () {
    final schema = _manifestSchema();
    const unsafePaths = [
      '/absolute.dll',
      r'C:\absolute.dll',
      'payload.dll:stream',
      'folder//file.dll',
      'folder/./file.dll',
      'folder/../file.dll',
      'NUL.txt',
      'folder/aux.dll',
      'folder/trailing.',
      'folder/trailing ',
      'folder/\u0001.dll',
    ];

    for (final path in unsafePaths) {
      final json = _validManifest()..['entryAssembly'] = path;
      expect(
        schema.validate(json).isValid,
        isFalse,
        reason: 'schema accepted $path',
      );
      expect(
        ModManifest.fromJson(json).validate().any((issue) => issue.isBlocking),
        isTrue,
        reason: 'domain accepted $path',
      );
    }
  });

  test('schema enforces complete SemVer 2.0.0 versions', () {
    final schema = _manifestSchema();
    for (final version in const [
      '1',
      '1.2',
      '01.2.3',
      '1.2.3-01',
      '1.2.3-alpha_beta',
    ]) {
      final json = _validManifest()..['version'] = version;
      expect(
        schema.validate(json).isValid,
        isFalse,
        reason: 'schema accepted $version',
      );
      expect(
        ModManifest.fromJson(json).validate().any((issue) => issue.isBlocking),
        isTrue,
        reason: 'domain accepted $version',
      );
    }
  });

  test('shared V5 fixtures agree across schema and domain validators', () {
    final root = _repoRoot();
    final schema = _manifestSchema();
    final fixtureRoot = _join(root.path, ['tests', 'fixtures', 'manifests']);
    final cases = File(_join(fixtureRoot, ['corpus.txt']))
        .readAsLinesSync()
        .map((line) => line.trim())
        .where((line) => line.isNotEmpty && !line.startsWith('#'));

    for (final testCase in cases) {
      final separator = testCase.indexOf(' ');
      final expectation = testCase.substring(0, separator);
      final fixtureName = testCase.substring(separator + 1).trim();
      final json =
          jsonDecode(File(_join(fixtureRoot, [fixtureName])).readAsStringSync())
              as Map<String, Object?>;
      final expectedValid = expectation == 'valid';
      final expectedSchemaValid = expectation != 'invalid-schema';
      final schemaValid = schema.validate(json).isValid;
      var domainValid = false;
      try {
        domainValid = ModManifest.fromJson(
          json,
        ).validate().every((issue) => !issue.isBlocking);
      } on FormatException {
        domainValid = false;
      } on TypeError {
        domainValid = false;
      }

      expect(
        schemaValid,
        expectedSchemaValid,
        reason: 'JSON Schema disagreed for $fixtureName',
      );
      expect(
        domainValid,
        expectedValid,
        reason: 'Dart validator disagreed for $fixtureName',
      );
    }
  });
}

JsonSchema _manifestSchema() {
  final root = _repoRoot();
  return JsonSchema.create(
    jsonDecode(
          File(
            _join(root.path, ['schemas', 'topiaforge.mod.schema.json']),
          ).readAsStringSync(),
        )
        as Map<String, Object?>,
  );
}

Map<String, Object?> _validManifest() => {
  'schemaVersion': 5,
  'name': 'sample.schema-parity',
  'displayName': 'Schema parity',
  'version': '1.2.3',
  'author': {'name': 'Test'},
  'entryAssembly': 'Sample.SchemaParity.dll',
  'entryType': 'Sample.SchemaParity.Mod',
  'supportedGameVersionRange': '*',
  'supportedLoaderVersionRange': '*',
  'supportedSdkVersionRange': '*',
};

Directory _repoRoot() {
  var directory = Directory.current.absolute;
  while (true) {
    if (File(_join(directory.path, ['TopiaForge.slnx'])).existsSync()) {
      return directory;
    }
    final parent = directory.parent;
    if (parent.path == directory.path) {
      throw StateError('Could not locate TopiaForge.slnx.');
    }
    directory = parent;
  }
}

String _join(String root, List<String> parts) {
  final separator = Platform.pathSeparator;
  return [root, ...parts].join(separator);
}
