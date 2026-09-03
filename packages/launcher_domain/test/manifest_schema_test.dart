import 'dart:convert';
import 'dart:io';

import 'package:json_schema/json_schema.dart';
import 'package:launcher_domain/launcher_domain.dart';
import 'package:test/test.dart';

void main() {
  test('the canonical alias is the current versioned schema', () {
    final root = _repoRoot();
    Map<String, Object?> readSchema(String name) =>
        jsonDecode(File(_join(root.path, ['schemas', name])).readAsStringSync())
            as Map<String, Object?>;

    final alias = readSchema('topiaforge.mod.schema.json');
    final versioned = readSchema('topiaforge.mod.v6.schema.json');
    expect(
      ((alias['properties'] as Map)['schemaVersion'] as Map)['const'],
      ModManifest.currentSchemaVersion,
      reason: 'the alias must declare the version current tooling emits',
    );

    for (final schema in [alias, versioned]) {
      schema.remove(r'$id');
      schema.remove('title');
      schema.remove('description');
    }
    expect(
      versioned,
      alias,
      reason:
          'the alias editors resolve and the frozen schema readers dispatch on '
          'must not be able to disagree',
    );
  });

  test('the retired V5 schema rejects every document', () {
    final root = _repoRoot();
    final retired = _manifestSchema('topiaforge.mod.v5.schema.json');

    // The same shape V4 was retired into. A schema that merely stopped matching
    // would report an unknown field; one that refuses outright can carry the
    // sentence that says what to run instead.
    for (final document in <Map<String, Object?>>[
      const {},
      _v6Manifest(),
      {..._v6Manifest(), 'schemaVersion': 5},
    ]) {
      expect(
        retired.validate(document).isValid,
        isFalse,
        reason: 'the retired V5 schema must reject $document',
      );
    }

    final raw =
        jsonDecode(
              File(
                _join(root.path, ['schemas', 'topiaforge.mod.v5.schema.json']),
              ).readAsStringSync(),
            )
            as Map<String, Object?>;
    expect(raw['not'], isEmpty);
    expect(raw['description'], contains('migrate-manifest'));
    expect(
      raw.containsKey('properties'),
      isFalse,
      reason:
          'a retired schema keeps no shape anyone could still author against',
    );
  });

  test('the versioned V6 schema is frozen and self-contained', () {
    final root = _repoRoot();
    final v6 =
        jsonDecode(
              File(
                _join(root.path, ['schemas', 'topiaforge.mod.v6.schema.json']),
              ).readAsStringSync(),
            )
            as Map<String, Object?>;

    expect(
      jsonEncode(v6),
      isNot(contains('/schemas/topiaforge.mod.schema.json')),
      reason:
          'a frozen versioned schema must never reference the mutable alias',
    );

    final properties = v6['properties']! as Map<String, Object?>;
    expect(
      (properties['schemaVersion']! as Map)['const'],
      ModManifest.manifestV6SchemaVersion,
    );
    expect(properties.containsKey('contributions'), isTrue);

    // Declared, and rejecting. additionalProperties alone would only say
    // "unknown field", which does not tell an author where those entries went.
    expect((properties['worldGamemodes']! as Map)['not'], isEmpty);
    expect(
      (properties['worldGamemodes']! as Map)['description'],
      contains('migrate-manifest'),
    );

    expect(
      jsonEncode(v6),
      isNot(contains('#/definitions/gamemode"')),
      reason: 'the V5 gamemode definition has no referent left in V6',
    );
  });

  test('the retired V6 worldGamemodes stub rejects any value', () {
    final schema = JsonSchema.create(
      jsonDecode(
            File(
              _join(_repoRoot().path, [
                'schemas',
                'topiaforge.mod.v6.schema.json',
              ]),
            ).readAsStringSync(),
          )
          as Map<String, Object?>,
    );
    for (final value in <Object?>[
      <Object?>[],
      [
        {'id': 'sample.mod.mode', 'name': 'Mode'},
      ],
      null,
    ]) {
      expect(
        schema.validate({..._v6Manifest(), 'worldGamemodes': value}).isValid,
        isFalse,
        reason: 'worldGamemodes: $value should be rejected outright',
      );
    }
  });

  test('checked-in manifests satisfy the current schema', () {
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

  test('shared fixtures agree across schema and domain validators', () {
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

JsonSchema _manifestSchema([String name = 'topiaforge.mod.schema.json']) {
  final root = _repoRoot();
  return JsonSchema.create(
    jsonDecode(File(_join(root.path, ['schemas', name])).readAsStringSync())
        as Map<String, Object?>,
  );
}

Map<String, Object?> _validManifest() => {
  'schemaVersion': ModManifest.currentSchemaVersion,
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

Map<String, Object?> _v6Manifest() => {
  'schemaVersion': 6,
  'name': 'sample.schema-parity',
  'displayName': 'Schema Parity',
  'version': '1.0.0',
  'author': {'name': 'Tester'},
  'entryAssembly': 'Sample.SchemaParity.dll',
  'entryType': 'Sample.SchemaParity.Mod',
  'supportedGameVersionRange': '*',
  'supportedLoaderVersionRange': '*',
  'supportedSdkVersionRange': '*',
};
