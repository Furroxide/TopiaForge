import 'dart:convert';
import 'dart:io';

import 'package:launcher_data/launcher_data.dart';
import 'package:path/path.dart' as p;
import 'package:test/test.dart';

void main() {
  late Directory root;
  late Directory project;
  late LocalDeveloperRepository repository;

  setUp(() {
    root = Directory.systemTemp.createTempSync('topiaforge-vpm-id-');
    project = Directory(p.join(root.path, 'project'));
    Directory(p.join(project.path, 'Packages')).createSync(recursive: true);
    repository = LocalDeveloperRepository(
      dataRoot: p.join(root.path, 'data'),
      repositoryRoot: p.join(root.path, 'repository'),
    );
  });

  tearDown(() {
    if (root.existsSync()) {
      root.deleteSync(recursive: true);
    }
  });

  test(
    'manifest shape rejects retired direct, locked, and nested ids',
    () async {
      final retired =
          'robo'
          'topia.vpm.retired';
      final manifest = File(
        p.join(project.path, 'Packages', 'vpm-manifest.json'),
      );
      for (final payload in [
        {
          'dependencies': {retired: '*'},
        },
        {
          'locked': {
            retired: {'version': '1.0.0'},
          },
        },
        {
          'locked': {
            'com.example.safe': {
              'version': '1.0.0',
              'dependencies': {retired: '1.0.0'},
            },
          },
        },
      ]) {
        final original = jsonEncode(payload);
        manifest.writeAsStringSync(original);

        await expectLater(
          repository.resolveUnityProject(project.path),
          throwsA(
            isA<StateError>().having(
              (error) => error.message,
              'message',
              contains('Invalid Packages/vpm-manifest.json'),
            ),
          ),
        );
        expect(manifest.readAsStringSync(), original);
      }
    },
  );

  test('add rejects a retired id before changing the manifest', () async {
    final manifest = File(p.join(project.path, 'Packages', 'vpm-manifest.json'))
      ..writeAsStringSync('{"dependencies":{},"locked":{}}');
    final original = manifest.readAsStringSync();

    await expectLater(
      repository.addUnityPackage(
        project.path,
        'robo'
            'topia.vpm.retired',
        '*',
      ),
      throwsStateError,
    );

    expect(manifest.readAsStringSync(), original);
    expect(
      Directory(
        p.join(
          project.path,
          'Packages',
          'robo'
              'topia.vpm.retired',
        ),
      ).existsSync(),
      isFalse,
    );
  });
}
