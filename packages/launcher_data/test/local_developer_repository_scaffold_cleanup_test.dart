import 'dart:io';

import 'package:launcher_data/launcher_data.dart';
import 'package:launcher_domain/launcher_domain.dart';
import 'package:path/path.dart' as p;
import 'package:test/test.dart';

/// `createModProject` checks everything before it creates anything, and a
/// failure while it writes removes only what that run created, so a retry
/// works. The templates are the real ones unless a case needs a broken one.
void main() {
  final realRepoRoot = p.normalize(p.join(Directory.current.path, '..', '..'));
  late Directory root;

  setUp(() {
    root = Directory.systemTemp.createTempSync('tf-scaffold-cleanup-');
  });
  tearDown(() => root.deleteSync(recursive: true));

  LocalDeveloperRepository repositoryAt(String repoRoot) =>
      LocalDeveloperRepository(
        dataRoot: p.join(root.path, 'data'),
        repositoryRoot: repoRoot,
      );

  List<String> namesIn(Directory folder) => [
    for (final entity in folder.listSync(followLinks: false))
      p.basename(entity.path),
  ]..sort();

  /// A repository root whose only template, [id], holds [files]; `minimal`
  /// then falls back to the built-in starter.
  Directory fakeRepository(String id, Map<String, List<int>> files) {
    final repo = Directory(p.join(root.path, 'repo'))..createSync();
    final template = Directory(p.join(repo.path, 'templates', 'mod', id))
      ..createSync(recursive: true);
    File(
      p.join(template.path, 'template.json'),
    ).writeAsStringSync('{"id": "$id", "label": "Fake $id"}');
    for (final MapEntry(key: name, value: bytes) in files.entries) {
      File(p.join(template.path, name)).writeAsBytesSync(bytes);
    }
    return repo;
  }

  test('an unknown template is refused before anything is created, even under '
      'a parent that is a file', () async {
    // Creating anything under a file fails, so only a check made before
    // creating can name the template.
    final parent = File(p.join(root.path, 'not-a-folder'))
      ..writeAsStringSync('sentinel');

    await expectLater(
      repositoryAt(realRepoRoot).createModProject(
        parentDirectory: parent.path,
        id: 'test.unknown',
        name: 'Unknown',
        options: const ModScaffoldOptions(template: 'nope'),
      ),
      throwsA(
        isA<StateError>().having(
          (error) => error.message,
          'message',
          'Unknown mod template "nope" (available: asset, gamemode, '
              'gameplay, minimal, multiplayer, service, ui, world).',
        ),
      ),
    );

    expect(parent.readAsStringSync(), 'sentinel');
    expect(namesIn(root), ['not-a-folder']);
  });

  test('an invalid id or name is refused before anything is created', () async {
    final parent = p.join(root.path, 'new');
    for (final (id, name, message) in [
      ('Bad Id', 'Fine', startsWith('Mod id "Bad Id" is not valid.')),
      ('x', 'Fine', startsWith('Mod id "x" is not valid.')),
      ('test.fine', '', 'The mod name must not be blank.'),
      ('test.fine', '  ', 'The mod name must not be blank.'),
      (
        'test.fine',
        'n' * 129,
        'The mod name must contain at most 128 Unicode characters.',
      ),
    ]) {
      await expectLater(
        repositoryAt(
          realRepoRoot,
        ).createModProject(parentDirectory: parent, id: id, name: name),
        throwsA(
          isA<StateError>().having(
            (error) => error.message,
            'message',
            message,
          ),
        ),
        reason: '$id with a name of ${name.length} characters',
      );
    }
    expect(namesIn(root), isEmpty);
  });

  test('a failure while copying the template removes only what this run '
      'created, and a retry works', () async {
    final repo = fakeRepository('broken', {
      'README.md': '# {{DISPLAY_NAME}}\n'.codeUnits,
      // A text file that is not UTF-8 fails the copy once the project
      // folder and its first files exist.
      '{{ASSEMBLY_NAME}}.csproj': [0xC3, 0x28, 0xFF],
    });
    // The run creates the last two folders of the parent; the two before
    // them were there already, one holding a file and one empty.
    final existing = Directory(p.join(root.path, 'existing'))..createSync();
    final sentinel = File(p.join(existing.path, 'sentinel.txt'))
      ..writeAsStringSync('keep');
    final keptEmpty = Directory(p.join(existing.path, 'kept-empty'))
      ..createSync();
    final parent = p.join(keptEmpty.path, 'made', 'by-this-run');

    await expectLater(
      repositoryAt(repo.path).createModProject(
        parentDirectory: parent,
        id: 'test.broken',
        name: 'Broken',
        options: const ModScaffoldOptions(template: 'broken'),
      ),
      throwsFormatException,
    );

    expect(namesIn(existing), ['kept-empty', 'sentinel.txt']);
    expect(sentinel.readAsStringSync(), 'keep');
    expect(namesIn(keptEmpty), isEmpty);

    final workspace = await repositoryAt(repo.path).createModProject(
      parentDirectory: parent,
      id: 'test.broken',
      name: 'Broken',
    );
    expect(workspace.projectRoot, p.join(parent, 'test.broken'));
    expect(
      File(p.join(parent, 'test.broken', 'topiaforge.mod.json')).existsSync(),
      isTrue,
    );
  });

  test('a parent path with .. after a folder that does not exist yet is made '
      'where it leads, and nothing else is', () async {
    final repo = fakeRepository('unused', const {});
    final work = Directory(p.join(root.path, 'work'))..createSync();

    final workspace = await repositoryAt(repo.path).createModProject(
      parentDirectory: p.join(work.path, 'missing', '..', 'mods'),
      id: 'test.dots',
      name: 'Dots',
    );

    expect(workspace.projectRoot, p.join(work.path, 'mods', 'test.dots'));
    expect(namesIn(work), ['mods']);
    expect(
      File(
        p.join(work.path, 'mods', 'test.dots', 'topiaforge.mod.json'),
      ).existsSync(),
      isTrue,
    );
  });

  test('a pre-existing project folder is refused and left as it was', () async {
    final existing = Directory(p.join(root.path, 'test.exists'))..createSync();
    File(p.join(existing.path, 'keep.txt')).writeAsStringSync('keep');

    await expectLater(
      repositoryAt(realRepoRoot).createModProject(
        parentDirectory: root.path,
        id: 'test.exists',
        name: 'Exists',
      ),
      throwsA(
        isA<StateError>().having(
          (error) => error.message,
          'message',
          'Project already exists: ${existing.path}',
        ),
      ),
    );

    expect(namesIn(root), ['test.exists']);
    expect(namesIn(existing), ['keep.txt']);
    expect(File(p.join(existing.path, 'keep.txt')).readAsStringSync(), 'keep');
  });

  test('a template path that leaves the project folder once its tokens are '
      'substituted is refused, and nothing stays', () async {
    final repo = fakeRepository('escaping', {
      '{{DISPLAY_NAME}}.txt': 'escaped'.codeUnits,
    });
    final parent = Directory(p.join(root.path, 'parent'))..createSync();

    await expectLater(
      repositoryAt(repo.path).createModProject(
        parentDirectory: parent.path,
        id: 'test.escape',
        name: '../../escaped',
        options: const ModScaffoldOptions(template: 'escaping'),
      ),
      throwsA(
        isA<StateError>().having(
          (error) => error.message,
          'message',
          contains('leaves the project folder'),
        ),
      ),
    );

    expect(namesIn(parent), isEmpty);
    expect(namesIn(root), ['parent', 'repo']);
  });
}
