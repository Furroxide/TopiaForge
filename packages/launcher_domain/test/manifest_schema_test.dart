import 'dart:convert';
import 'dart:io';

import 'package:json_schema/json_schema.dart';
import 'package:launcher_domain/launcher_domain.dart';
import 'package:test/test.dart';

void main() {
  test('checked-in manifests satisfy schema v3', () {
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
  'schemaVersion': 3,
  'name': 'sample.schema-parity',
  'displayName': 'Schema parity',
  'version': '1.2.3',
  'author': {'name': 'Test'},
  'entryAssembly': 'Sample.SchemaParity.dll',
  'entryType': 'Sample.SchemaParity.Mod',
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
