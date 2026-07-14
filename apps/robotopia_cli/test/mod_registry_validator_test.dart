import 'dart:convert';
import 'dart:io';

import 'package:archive/archive.dart';
import 'package:launcher_domain/launcher_domain.dart';
import 'package:path/path.dart' as p;
import 'package:robotopia/src/mod_registry_index_builder.dart';
import 'package:robotopia/src/registry_entry_builder.dart';
import 'package:test/test.dart';

void main() {
  late Directory temp;
  late Directory entries;
  Future<List<int>> Function(Uri uri)? fetchBytes;

  setUp(() async {
    temp = await Directory.systemTemp.createTemp('robotopia-validator-test-');
    entries = Directory(p.join(temp.path, 'registry'))..createSync();
    fetchBytes = null;
  });

  tearDown(() async {
    if (await temp.exists()) {
      await temp.delete(recursive: true);
    }
  });

  Future<ModRegistryValidationResult> validate({
    bool download = false,
    List<String> onlyFiles = const [],
    String modsDirectory = '',
  }) {
    return ModRegistryValidator(fetchBytes: fetchBytes).validate(
      ModRegistryValidationOptions(
        entriesDirectory: entries.path,
        modsDirectory: modsDirectory,
        onlyFiles: onlyFiles,
        download: download,
      ),
    );
  }

  test('valid entry passes structural validation', () async {
    _writeEntry(entries, id: 'author.jetpack', version: '1.0.0');

    final result = await validate();

    expect(result.reports.single.issues, isEmpty);
    expect(result.ok, isTrue);
  });

  test('filename must be the lowercase id', () async {
    _writeEntry(
      entries,
      id: 'author.jetpack',
      version: '1.0.0',
      fileName: 'Jetpack.json',
    );

    final result = await validate();

    expect(result.ok, isFalse);
    expect(
      result.reports.single.issues.map((issue) => issue.message).join(' '),
      contains('must be named "author.jetpack.json"'),
    );
  });

  test('reserved prefixes and first-party collisions are errors', () async {
    final mods = Directory(p.join(temp.path, 'mods', 'Core'))
      ..createSync(recursive: true);
    File(p.join(mods.path, 'robotopia.mod.json')).writeAsStringSync(
      jsonEncode(_manifestJson(id: 'first.party', version: '1.0.0')),
    );
    _writeEntry(entries, id: 'sample.thing', version: '1.0.0');
    _writeEntry(entries, id: 'first.party', version: '2.0.0');

    final result = await validate(modsDirectory: p.join(temp.path, 'mods'));
    final all = result.reports
        .expand((report) => report.issues)
        .map((issue) => issue.message)
        .join(' ');

    expect(result.ok, isFalse);
    expect(all, contains('reserved'));
    expect(all, contains('collides with a first-party mod'));
  });

  test('missing and unsatisfiable dependencies are reported', () async {
    _writeEntry(
      entries,
      id: 'author.jetpack',
      version: '1.0.0',
      dependencies: {'missing.dep': '*', 'author.lib': '>=2.0.0'},
    );
    _writeEntry(entries, id: 'author.lib', version: '1.0.0');

    final result = await validate();
    final report = result.reports.firstWhere(
      (item) => item.fileName == 'author.jetpack.json',
    );
    final messages = report.issues.map((issue) => issue.message).join(' ');

    expect(messages, contains('"missing.dep" is not in the registry'));
    expect(messages, contains('No known version of "author.lib"'));
    expect(result.ok, isFalse);
  });

  test('unsatisfied first-party dependency range is only a warning', () async {
    final mods = Directory(p.join(temp.path, 'mods', 'Kit'))
      ..createSync(recursive: true);
    File(p.join(mods.path, 'robotopia.mod.json')).writeAsStringSync(
      jsonEncode(_manifestJson(id: 'robotopia.robotkit', version: '0.7.0')),
    );
    _writeEntry(
      entries,
      id: 'author.jetpack',
      version: '1.0.0',
      dependencies: {'robotopia.robotkit': '>=9.0.0'},
    );

    final result = await validate(modsDirectory: p.join(temp.path, 'mods'));
    final issue = result.reports.single.issues.single;

    expect(issue.severity, IssueSeverity.warning);
    expect(issue.message, contains('current first-party version only'));
    expect(result.ok, isTrue);
  });

  group('download checks against an injected HTTPS package host', () {
    late Map<String, List<int>> hosted;

    setUp(() {
      hosted = {};
      fetchBytes = (uri) async {
        final bytes = hosted[uri.path];
        if (bytes == null) {
          throw HttpException('HTTP 404', uri: uri);
        }
        return bytes;
      };
    });

    String hostedUrl(String path) => 'https://mods.example.test$path';

    test('matching bytes pass; wrong sha and 404 fail', () async {
      final goodBytes = _packageBytes(id: 'author.good', version: '1.0.0');
      hosted['/good.robotopiamod'] = goodBytes;
      _writeEntryForBytes(
        entries,
        bytes: goodBytes,
        url: hostedUrl('/good.robotopiamod'),
      );

      final tampered = _packageBytes(id: 'author.bad', version: '1.0.0');
      hosted['/bad.robotopiamod'] = _packageBytes(
        id: 'author.bad',
        version: '1.0.1',
      );
      _writeEntryForBytes(
        entries,
        bytes: tampered,
        url: hostedUrl('/bad.robotopiamod'),
      );

      _writeEntryForBytes(
        entries,
        bytes: _packageBytes(id: 'author.gone', version: '1.0.0'),
        url: hostedUrl('/gone.robotopiamod'),
      );

      final result = await validate(download: true);
      Map<String, String> messages() => {
        for (final report in result.reports)
          report.fileName: report.issues
              .map((issue) => issue.message)
              .join(' '),
      };

      expect(messages()['author.good.json'], isEmpty);
      expect(
        messages()['author.bad.json'],
        contains('packageSha256 does not match'),
      );
      expect(messages()['author.gone.json'], contains('Download failed'));
      expect(result.ok, isFalse);
    });

    test('inline manifest must match the packaged manifest', () async {
      final bytes = _packageBytes(id: 'author.good', version: '1.0.0');
      hosted['/good.robotopiamod'] = bytes;
      _writeEntryForBytes(
        entries,
        bytes: bytes,
        url: hostedUrl('/good.robotopiamod'),
        mutate: (json) {
          final versions = (json['versions'] as List).cast<Map>();
          final manifest = (versions.first['manifest'] as Map)
              .cast<String, Object?>();
          manifest['description'] = 'Sneakily edited after add-entry.';
        },
      );

      final result = await validate(download: true);

      expect(
        result.reports.single.issues.single.message,
        contains('inline manifest differs'),
      );
    });

    test('--only limits downloads to the named files', () async {
      final good = _packageBytes(id: 'author.good', version: '1.0.0');
      hosted['/good.robotopiamod'] = good;
      _writeEntryForBytes(
        entries,
        bytes: good,
        url: hostedUrl('/good.robotopiamod'),
      );
      // Broken URL, but not selected — must not produce download issues.
      _writeEntryForBytes(
        entries,
        bytes: _packageBytes(id: 'author.other', version: '1.0.0'),
        url: hostedUrl('/missing.robotopiamod'),
      );

      final result = await validate(
        download: true,
        onlyFiles: ['author.good.json'],
      );
      final other = result.reports.firstWhere(
        (report) => report.fileName == 'author.other.json',
      );

      expect(other.issues, isEmpty);
      expect(result.ok, isTrue);
    });
  });
}

Map<String, Object?> _manifestJson({
  required String id,
  required String version,
  Map<String, String> dependencies = const {},
}) {
  return {
    'schemaVersion': 2,
    'name': id,
    'displayName': id,
    'version': version,
    'author': {'name': 'Tester'},
    'license': 'MIT',
    'licenseFiles': ['LICENSE'],
    'entryAssembly': 'Mod.dll',
    'entryType': 'Test.Mod',
    if (dependencies.isNotEmpty) 'vpmDependencies': dependencies,
  };
}

List<int> _packageBytes({
  required String id,
  required String version,
  Map<String, String> dependencies = const {},
}) {
  final archive = Archive()
    ..addFile(
      ArchiveFile.string(
        'robotopia.mod.json',
        jsonEncode(
          _manifestJson(id: id, version: version, dependencies: dependencies),
        ),
      ),
    )
    ..addFile(ArchiveFile.string('LICENSE', 'MIT test fixture license'))
    ..addFile(ArchiveFile.string('Mod.dll', 'dll-bytes-$id-$version'));
  return ZipEncoder().encode(archive);
}

void _writeEntry(
  Directory entries, {
  required String id,
  required String version,
  Map<String, String> dependencies = const {},
  String? fileName,
}) {
  final result = buildRegistryEntry(
    package: readModPackage(
      _packageBytes(id: id, version: version, dependencies: dependencies),
    ),
    downloadUrl: 'https://mods.example.com/$id-$version.robotopiamod',
  );
  final json = const JsonEncoder.withIndent(
    '  ',
  ).convert(result.entryFile!.toJson());
  File(
    p.join(entries.path, fileName ?? '${id.toLowerCase()}.json'),
  ).writeAsStringSync('$json\n');
}

void _writeEntryForBytes(
  Directory entries, {
  required List<int> bytes,
  required String url,
  void Function(Map<String, Object?> json)? mutate,
}) {
  final package = readModPackage(bytes);
  final result = buildRegistryEntry(package: package, downloadUrl: url);
  final json = result.entryFile!.toJson();
  mutate?.call(json);
  final text = const JsonEncoder.withIndent('  ').convert(json);
  File(
    p.join(entries.path, '${package.manifest.id.toLowerCase()}.json'),
  ).writeAsStringSync('$text\n');
}
