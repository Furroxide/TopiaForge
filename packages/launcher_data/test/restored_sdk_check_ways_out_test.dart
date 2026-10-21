import 'dart:io';

import 'package:launcher_data/launcher_data.dart';
import 'package:path/path.dart' as p;
import 'package:test/test.dart';

/// After `topiaforge restore`, each TF1103 that a correct project can meet
/// names a step that gets past it: doing what the message says, as an author
/// would, makes the next restore pass. Each case restores a mod scaffolded
/// from the real template against the cached SDK feed. The messages name a
/// lock file with the platform's separator.
void main() {
  final repoRoot = p.normalize(p.join(Directory.current.path, '..', '..'));
  late Directory root;
  late LocalDeveloperRepository repository;
  late String projectRoot;
  late String name;
  late File project;

  setUp(() async {
    root = Directory.systemTemp.createTempSync('tf-restored-sdk-way-');
    repository = LocalDeveloperRepository(
      dataRoot: p.join(root.path, 'data'),
      repositoryRoot: repoRoot,
    );
    final workspace = await repository.createModProject(
      parentDirectory: root.path,
      id: 'test.sdkway',
      name: 'SDK Way',
    );
    projectRoot = workspace.projectRoot;
    project = Directory(projectRoot).listSync().whereType<File>().singleWhere(
      (file) => file.path.endsWith('.csproj'),
    );
    name = p.basename(project.path);
  });
  tearDown(() => root.deleteSync(recursive: true));

  const restoreTimeout = Timeout(Duration(minutes: 5));

  Future<List<String>> restore() async => [
    for (final issue in (await repository.resolveDeveloperProject(
      projectRoot,
    )).issues)
      if (issue.isBlocking) issue.message,
  ];

  /// Declares [path] as the mod project's `NuGetLockFilePath`, in its first
  /// `<PropertyGroup>`, or replaces the path it declares.
  void declare(String path) {
    final text = project.readAsStringSync();
    project.writeAsStringSync(
      text.contains('<NuGetLockFilePath>')
          ? text.replaceFirst(
              RegExp('<NuGetLockFilePath>[^<]*</NuGetLockFilePath>'),
              '<NuGetLockFilePath>$path</NuGetLockFilePath>',
            )
          : text.replaceFirst(
              '<PropertyGroup>',
              '<PropertyGroup>\n'
                  '    <NuGetLockFilePath>$path</NuGetLockFilePath>',
            ),
    );
  }

  /// Writes `Directory.Build.targets`, setting the mod project's
  /// `NuGetLockFilePath` to [path] after its own declaration.
  File overrideDeclared(String path) =>
      File(p.join(projectRoot, 'Directory.Build.targets'))..writeAsStringSync(
        '<Project>\n'
        '  <PropertyGroup Condition="\'\$(MSBuildProjectName)\' == '
        '\'${p.basenameWithoutExtension(name)}\'">\n'
        '    <NuGetLockFilePath>$path</NuGetLockFilePath>\n'
        '  </PropertyGroup>\n'
        '</Project>\n',
      );

  /// Writes `Directory.Build.props`, moving every project's `obj` elsewhere.
  File moveObj() =>
      File(p.join(projectRoot, 'Directory.Build.props'))..writeAsStringSync(
        '<Project>\n'
        '  <PropertyGroup>\n'
        '    <BaseIntermediateOutputPath>\$(MSBuildProjectDirectory)/elsewhere/'
        '</BaseIntermediateOutputPath>\n'
        '  </PropertyGroup>\n'
        '</Project>\n',
      );

  /// A lock file left over is hours or days old, not the seconds a test
  /// takes, so date every NuGet lock file back.
  void ageLockFiles() {
    final hourAgo = DateTime.now().subtract(const Duration(hours: 1));
    for (final file in Directory(projectRoot).listSync(recursive: true)) {
      final fileName = p.basename(file.path);
      if (file is File &&
          fileName.endsWith('.lock.json') &&
          !fileName.startsWith('topiaforge.')) {
        file.setLastModifiedSync(hourAgo);
      }
    }
  }

  test('an obj left beside the project that names an old lock file: delete '
      'it, as TF1103 says', () async {
    declare('locks/old.lock.json');
    expect(await restore(), isEmpty);
    declare('locks/new.lock.json');
    moveObj();
    ageLockFiles();

    expect(await restore(), [
      allOf(
        startsWith(
          'TF1103: ${p.join('locks', 'old.lock.json')}, the NuGet lock file '
          'found for $name, was not written by its restore',
        ),
        contains(
          'the obj/project.assets.json beside $name that names it is left '
          'from an earlier restore, so delete that obj folder',
        ),
      ),
    ]);
    Directory(p.join(projectRoot, 'obj')).deleteSync(recursive: true);
    expect(await restore(), isEmpty);
  }, timeout: restoreTimeout);

  test('a declared lock path an import overrides, with obj moved: make the '
      'declaration apply, as TF1103 says', () async {
    moveObj();
    declare('locks/declared.lock.json');
    expect(await restore(), isEmpty);
    final targets = overrideDeclared('locks/imported.lock.json');
    ageLockFiles();

    expect(await restore(), [
      allOf(
        startsWith(
          'TF1103: ${p.join('locks', 'declared.lock.json')}, the NuGet lock '
          'file found for $name, was not written by its restore',
        ),
        contains(
          '$name declares that NuGetLockFilePath puts it there, but a later '
          'setting, a condition or an import sets another path, or none. '
          'Make the NuGetLockFilePath that $name declares the one that '
          'applies',
        ),
      ),
    ]);
    targets.deleteSync();
    expect(await restore(), isEmpty);
  }, timeout: restoreTimeout);

  test('a declared lock path an import blanks, with obj beside: make the '
      'declaration apply, as TF1103 says', () async {
    declare('locks/declared.lock.json');
    // NuGet then writes its own default lock file and records no path, so
    // neither keeping obj beside nor declaring the path again gets past it.
    final targets = overrideDeclared('');

    expect(await restore(), [
      allOf(
        startsWith(
          'TF1103: No NuGet lock file was found for $name after its restore: '
          'restore looked for ${p.join('locks', 'declared.lock.json')}, where '
          '$name declares that NuGetLockFilePath puts it.',
        ),
        contains(
          'NuGet did not write it there, so a later setting, a condition or '
          'an import sets NuGetLockFilePath to another path, or to none. Make '
          'the NuGetLockFilePath that $name declares the one that applies',
        ),
        endsWith('See https://docs.topiaforge.dev/diagnostics/TF1103'),
      ),
    ]);
    expect(
      File(p.join(projectRoot, 'packages.lock.json')).existsSync(),
      isTrue,
    );
    targets.deleteSync();
    expect(await restore(), isEmpty);
    expect(
      File(p.join(projectRoot, 'locks', 'declared.lock.json')).existsSync(),
      isTrue,
    );
  }, timeout: restoreTimeout);
}
