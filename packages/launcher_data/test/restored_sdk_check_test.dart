import 'dart:io';

import 'package:archive/archive.dart';
import 'package:launcher_data/launcher_data.dart';
import 'package:launcher_domain/launcher_domain.dart';
import 'package:path/path.dart' as p;
import 'package:test/test.dart';

/// After `topiaforge restore`, what NuGet resolved for each pinned SDK package,
/// read from the lock file a real restore wrote, against the pin. Each case
/// changes the version from where the pin cannot see it, as the forms #223
/// left open do, and restores against the cached SDK feed and a local one.
void main() {
  final repoRoot = p.normalize(p.join(Directory.current.path, '..', '..'));
  late Directory root;
  late LocalDeveloperRepository repository;
  late String projectRoot;
  late String projectName;

  setUp(() async {
    root = Directory.systemTemp.createTempSync('tf-restored-sdk-');
    repository = LocalDeveloperRepository(
      dataRoot: p.join(root.path, 'data'),
      repositoryRoot: repoRoot,
    );
    final workspace = await repository.createModProject(
      parentDirectory: root.path,
      id: 'test.sdkcheck',
      name: 'SDK Check',
    );
    projectRoot = workspace.projectRoot;
    projectName = p.basenameWithoutExtension(
      Directory(projectRoot)
          .listSync()
          .whereType<File>()
          .singleWhere((file) => file.path.endsWith('.csproj'))
          .path,
    );
  });
  tearDown(() => root.deleteSync(recursive: true));

  const pinned = TopiaForgeRuntimeVersions.sdkVersion;
  const restoreTimeout = Timeout(Duration(minutes: 5));

  /// Writes a `Directory.Build.targets` that changes the main project's
  /// `TopiaForge.Mods.Abstractions` item with [attributes], leaving its test
  /// project alone.
  void overrideAbstractions(String attributes) {
    File(p.join(projectRoot, 'Directory.Build.targets')).writeAsStringSync('''
<Project>
  <ItemGroup Condition="'\$(MSBuildProjectName)' == '$projectName'">
    <PackageReference $attributes />
  </ItemGroup>
</Project>
''');
  }

  List<String> blocking(DeveloperWorkspace workspace) => [
    for (final issue in workspace.issues)
      if (issue.isBlocking) issue.message,
  ];

  test(
    'a version set in Directory.Build.targets is reported with both versions '
    'and the file',
    () async {
      _localFeed(root, projectRoot, 'TopiaForge.Mods.Abstractions', '0.0.1');
      overrideAbstractions(
        'Update="TopiaForge.Mods.Abstractions" Version="0.0.1"',
      );

      final workspace = await repository.resolveDeveloperProject(projectRoot);

      expect(blocking(workspace), [
        allOf(
          startsWith(
            'TF1102: NuGet resolved TopiaForge.Mods.Abstractions 0.0.1 for '
            '$projectName.csproj, but the SDK is pinned to $pinned.',
          ),
          contains('Directory.Build.targets, which names it'),
          contains('Make it use $pinned'),
          endsWith('See https://docs.topiaforge.dev/diagnostics/TF1102'),
        ),
      ]);
      expect(workspace.issues.single.subjectId, 'TopiaForge.Mods.Abstractions');
    },
    timeout: restoreTimeout,
  );

  test(
    'a pinned package removed from outside its item is reported',
    () async {
      overrideAbstractions('Remove="TopiaForge.Mods.Abstractions"');

      final workspace = await repository.resolveDeveloperProject(projectRoot);

      expect(blocking(workspace), [
        allOf(
          startsWith(
            'TF1102: NuGet resolved no TopiaForge.Mods.Abstractions for '
            '$projectName.csproj, though $projectName.csproj is pinned to it at '
            '$pinned.',
          ),
          contains('Directory.Build.targets, which names it'),
        ),
      ]);
    },
    timeout: restoreTimeout,
  );

  test('a version set where no implicit import names the package lists the '
      'causes, the implicit imports first', () async {
    _localFeed(root, projectRoot, 'TopiaForge.Mods.Abstractions', '0.0.1');
    // Neither file spells the package's id out, so neither can be named.
    _directoryBuildProps(projectRoot, ['<SdkPart>Abstractions</SdkPart>']);
    overrideAbstractions(
      r'Update="TopiaForge.Mods.$(SdkPart)" Version="0.0.1"',
    );

    final workspace = await repository.resolveDeveloperProject(projectRoot);

    expect(blocking(workspace), [
      allOf(
        startsWith(
          'TF1102: NuGet resolved TopiaForge.Mods.Abstractions 0.0.1 for '
          '$projectName.csproj, but the SDK is pinned to $pinned.',
        ),
        contains(
          'Something outside its PackageReference sets that: a '
          'Directory.Build.props, Directory.Build.targets or '
          'Directory.Packages.props above $projectName.csproj, a property '
          'or item expression in $projectName.csproj, another imported '
          '.props or .targets file, or a target. Make it use $pinned',
        ),
      ),
    ]);
  }, timeout: restoreTimeout);

  test('a lock file NuGetLockFilePath moves is read where the restore recorded '
      'it', () async {
    _directoryBuildProps(projectRoot, [
      '<NuGetLockFilePath>locks/project.lock.json</NuGetLockFilePath>',
    ]);

    final workspace = await repository.resolveDeveloperProject(projectRoot);

    expect(blocking(workspace), isEmpty);
    expect(
      File(p.join(projectRoot, 'locks', 'project.lock.json')).existsSync(),
      isTrue,
    );
  }, timeout: restoreTimeout);

  test(
    'a lock file that cannot be found is reported, never taken as a pass',
    () async {
      // With the restore's record moved out of `obj` too, nothing says where
      // NuGet put the lock file.
      _directoryBuildProps(projectRoot, [
        r'<BaseIntermediateOutputPath>$(MSBuildProjectDirectory)/elsewhere/'
            '</BaseIntermediateOutputPath>',
        '<NuGetLockFilePath>locks/project.lock.json</NuGetLockFilePath>',
      ]);

      final workspace = await repository.resolveDeveloperProject(projectRoot);

      // The file applies to the mod project and its test project alike. The
      // message names the files restore looked for and both ways out.
      expect(blocking(workspace), [
        for (final project in [projectName, '$projectName.Tests'])
          allOf(
            startsWith(
              'TF1103: No NuGet lock file was found for $project.csproj after '
              'its restore: restore looked for packages.$project.lock.json '
              'and packages.lock.json beside $project.csproj, and '
              'obj/project.assets.json recorded no other path.',
            ),
            contains('keep obj beside $project.csproj'),
            contains(
              'declare NuGetLockFilePath in $project.csproj itself as a '
              'literal path',
            ),
            endsWith('See https://docs.topiaforge.dev/diagnostics/TF1103'),
          ),
      ]);
    },
    timeout: restoreTimeout,
  );

  test('a lock file NuGetLockFilePath moves is read where the project declares '
      'it, with obj elsewhere', () async {
    _directoryBuildProps(projectRoot, [
      r'<BaseIntermediateOutputPath>$(MSBuildProjectDirectory)/elsewhere/'
          '</BaseIntermediateOutputPath>',
    ]);
    final project = File(p.join(projectRoot, '$projectName.csproj'));
    project.writeAsStringSync(
      project.readAsStringSync().replaceFirst(
        '<PropertyGroup>',
        '<PropertyGroup>\n'
            '    <NuGetLockFilePath>locks/project.lock.json'
            '</NuGetLockFilePath>',
      ),
    );

    final workspace = await repository.resolveDeveloperProject(projectRoot);

    expect(blocking(workspace), isEmpty);
    expect(
      File(p.join(projectRoot, 'locks', 'project.lock.json')).existsSync(),
      isTrue,
    );
    expect(
      File(p.join(projectRoot, 'obj', 'project.assets.json')).existsSync(),
      isFalse,
    );
  }, timeout: restoreTimeout);

  test(
    'a project-named lock file, which NuGet uses instead, is read and passes',
    () async {
      final named = File(p.join(projectRoot, 'packages.$projectName.lock.json'))
        ..writeAsStringSync('{"version": 1, "dependencies": {}}');

      final workspace = await repository.resolveDeveloperProject(projectRoot);

      expect(blocking(workspace), isEmpty);
      expect(
        named.readAsStringSync(),
        contains('TopiaForge.Mods.Abstractions'),
      );
      expect(
        File(p.join(projectRoot, 'packages.lock.json')).existsSync(),
        isFalse,
      );
    },
    timeout: restoreTimeout,
  );

  test(
    'pack refuses a project that restores another SDK version',
    () async {
      overrideAbstractions('Remove="TopiaForge.Mods.Abstractions"');

      await expectLater(
        repository.packProject(
          projectRoot,
          outputDir: p.join(root.path, 'out'),
        ),
        throwsA(
          isA<StateError>().having(
            (error) => error.message,
            'message',
            startsWith(
              'TF1102: NuGet resolved no TopiaForge.Mods.Abstractions',
            ),
          ),
        ),
      );
      expect(Directory(p.join(root.path, 'out')).existsSync(), isFalse);
    },
    timeout: restoreTimeout,
  );
}

/// Writes [projectRoot]'s `Directory.Build.props` with [properties].
void _directoryBuildProps(String projectRoot, List<String> properties) {
  File(p.join(projectRoot, 'Directory.Build.props')).writeAsStringSync(
    [
      '<Project>',
      '  <PropertyGroup>',
      for (final property in properties) '    $property',
      '  </PropertyGroup>',
      '</Project>',
      '',
    ].join('\n'),
  );
}

/// A local NuGet source holding a stand-in [id] [version], which the project's
/// `nuget.config` adds.
void _localFeed(Directory root, String projectRoot, String id, String version) {
  final feed = Directory(p.join(root.path, 'local-feed'))..createSync();
  final nuspec =
      '''
<?xml version="1.0" encoding="utf-8"?>
<package xmlns="http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd">
  <metadata>
    <id>$id</id>
    <version>$version</version>
    <authors>Sam</authors>
    <description>A stand-in package for a restore test.</description>
  </metadata>
</package>
''';
  final archive = Archive()..addFile(ArchiveFile.string('$id.nuspec', nuspec));
  File(
    p.join(feed.path, '${id.toLowerCase()}.$version.nupkg'),
  ).writeAsBytesSync(ZipEncoder().encode(archive));
  File(p.join(projectRoot, 'nuget.config')).writeAsStringSync('''
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <add key="local" value="${feed.path}" />
  </packageSources>
</configuration>
''');
}
