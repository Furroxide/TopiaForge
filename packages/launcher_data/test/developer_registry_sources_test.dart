import 'dart:convert';
import 'dart:io';

import 'package:archive/archive.dart';
import 'package:launcher_data/launcher_data.dart';
import 'package:launcher_domain/launcher_domain.dart';
import 'package:path/path.dart' as p;
import 'package:test/test.dart';

void main() {
  late Directory temp;
  late LocalDeveloperRepository repository;

  setUp(() async {
    temp = await Directory.systemTemp.createTemp('topiaforge-dev-sources-');
    repository = LocalDeveloperRepository(
      dataRoot: p.join(temp.path, 'devdata'),
      repositoryRoot: p.join(temp.path, 'fake-repo'),
    );
  });

  tearDown(() async {
    if (await temp.exists()) {
      await temp.delete(recursive: true);
    }
  });

  test('a dead package source degrades to a non-blocking issue', () async {
    final workspace = await repository.createModProject(
      parentDirectory: p.join(temp.path, 'projects'),
      id: 'author.jet',
      name: 'Jet',
    );
    final projectRoot = workspace.projectRoot;

    final goodSource = Directory(p.join(temp.path, 'good-source'))
      ..createSync();
    _writePackage(goodSource, id: 'author.lib', version: '1.0.0');
    await repository.addProjectPackageSource(
      projectRoot,
      PackageSource(id: 'good', name: 'Good', url: goodSource.path),
    );
    await repository.addProjectPackageSource(
      projectRoot,
      PackageSource(
        id: 'bad',
        name: 'Bad',
        url: p.join(temp.path, 'missing-registry.json'),
      ),
    );
    await repository.addProjectDependency(
      projectRoot,
      const ModDependency(id: 'author.lib'),
    );

    final resolved = await repository.resolveDeveloperProject(
      projectRoot,
      restore: true,
    );

    expect(
      resolved.lock?.packages.map((package) => package.id),
      contains('author.lib'),
      reason: 'the healthy source still resolves',
    );
    final sourceIssue = resolved.issues.singleWhere(
      (issue) => issue.subjectId == 'bad',
    );
    expect(sourceIssue.severity, IssueSeverity.warning);
    expect(sourceIssue.isBlocking, isFalse);
    expect(resolved.issues.where((issue) => issue.isBlocking), isEmpty);
  });

  for (final formatVersion in <int?>[null, 1]) {
    final label = formatVersion == null ? 'missing' : 'version 1';
    test('developer flat registry rejects $label formatVersion', () async {
      final workspace = await repository.createModProject(
        parentDirectory: p.join(temp.path, 'projects'),
        id: 'author.jet',
        name: 'Jet',
      );
      final sourceId = 'retired-${formatVersion ?? 'missing'}';
      final index = File(p.join(temp.path, '$sourceId.json'));
      final payload = <String, Object?>{'mods': <Object?>[]};
      if (formatVersion != null) {
        payload['formatVersion'] = formatVersion;
      }
      index.writeAsStringSync(jsonEncode(payload));
      await repository.addProjectPackageSource(
        workspace.projectRoot,
        PackageSource(id: sourceId, name: 'Retired', url: index.path),
      );

      final resolved = await repository.resolveDeveloperProject(
        workspace.projectRoot,
        restore: false,
      );
      final issue = resolved.issues.singleWhere(
        (item) => item.subjectId == sourceId,
      );

      expect(issue.severity, IssueSeverity.warning);
      expect(issue.message, contains('formatVersion 2'));
    });
  }
}

void _writePackage(
  Directory directory, {
  required String id,
  required String version,
}) {
  directory.createSync(recursive: true);
  final archive = Archive()
    ..addFile(
      ArchiveFile.string(
        'topiaforge.mod.json',
        jsonEncode({
          'schemaVersion': 5,
          'name': id,
          'displayName': id,
          'version': version,
          'author': {'name': 'Tester'},
          'entryAssembly': 'Mod.dll',
          'entryType': 'Test.Mod',
          'supportedGameVersionRange': '*',
          'supportedLoaderVersionRange': '*',
          'supportedSdkVersionRange': '*',
        }),
      ),
    )
    ..addFile(ArchiveFile.string('Mod.dll', 'dll'));
  File(
    p.join(directory.path, '$id-$version.topiaforgemod'),
  ).writeAsBytesSync(ZipEncoder().encode(archive));
}
