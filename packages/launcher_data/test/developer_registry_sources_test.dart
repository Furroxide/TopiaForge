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
    temp = await Directory.systemTemp.createTemp('robotopia-dev-sources-');
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
        'robotopia.mod.json',
        jsonEncode({
          'schemaVersion': 2,
          'name': id,
          'displayName': id,
          'version': version,
          'author': {'name': 'Tester'},
          'entryAssembly': 'Mod.dll',
          'entryType': 'Test.Mod',
        }),
      ),
    )
    ..addFile(ArchiveFile.string('Mod.dll', 'dll'));
  File(
    p.join(directory.path, '$id-$version.robotopiamod'),
  ).writeAsBytesSync(ZipEncoder().encode(archive));
}
