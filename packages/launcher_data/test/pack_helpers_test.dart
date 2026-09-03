import 'dart:convert';
import 'dart:io';

import 'package:archive/archive.dart';
import 'package:launcher_data/launcher_data.dart';
import 'package:launcher_domain/launcher_domain.dart';
import 'package:path/path.dart' as p;
import 'package:test/test.dart';

void main() {
  late Directory root;
  late Directory repoRoot;
  late Directory projectDir;
  late LocalDeveloperRepository repository;

  setUp(() {
    root = Directory.systemTemp.createTempSync('pack_helpers_test');
    repoRoot = Directory(p.join(root.path, 'repo'))..createSync();
    File(p.join(repoRoot.path, 'global.json')).writeAsStringSync(
      jsonEncode({
        'sdk': {'version': '10.0.301', 'rollForward': 'disable'},
      }),
    );
    projectDir = Directory(p.join(root.path, 'sample.mod'))..createSync();
    repository = LocalDeveloperRepository(
      dataRoot: p.join(root.path, 'data'),
      repositoryRoot: repoRoot.path,
    );
  });

  tearDown(() {
    if (root.existsSync()) {
      root.deleteSync(recursive: true);
    }
  });

  Map<String, Object?> manifestJson() => {
    'schemaVersion': ModManifest.currentSchemaVersion,
    'name': 'sample.mod',
    'displayName': 'Sample Mod',
    'version': '1.2.3',
    'author': {'name': 'Tester'},
    'entryAssembly': 'Sample.dll',
    'entryType': 'Sample.Entry',
    'supportedGameVersionRange': '*',
    'supportedLoaderVersionRange': '*',
    'supportedSdkVersionRange': '*',
  };

  void writeManifest([Map<String, Object?>? overrides]) {
    File(
      p.join(projectDir.path, 'topiaforge.mod.json'),
    ).writeAsStringSync(jsonEncode({...manifestJson(), ...?overrides}));
  }

  Archive readPackage(String path) =>
      ZipDecoder().decodeBytes(File(path).readAsBytesSync());

  test('manifest-only pack ships the project tree minus build dirs', () async {
    writeManifest();
    File(p.join(projectDir.path, 'Sample.dll')).writeAsStringSync('payload');
    File(
      p.join(projectDir.path, 'assets', 'texture.png'),
    ).createSync(recursive: true);
    File(
      p.join(projectDir.path, 'bin', 'ignored.dll'),
    ).createSync(recursive: true);
    File(
      p.join(projectDir.path, 'obj', 'ignored.cache'),
    ).createSync(recursive: true);

    final packagePath = await repository.packModDirectory(projectDir.path);

    expect(p.basename(packagePath), 'sample.mod-1.2.3.topiaforgemod');
    final archive = readPackage(packagePath);
    final names = archive.files.map((file) => file.name).toSet();
    expect(names, contains('topiaforge.mod.json'));
    expect(names, contains('Sample.dll'));
    expect(names, contains('assets/texture.png'));
    expect(names.where((name) => name.startsWith('bin/')), isEmpty);
    expect(names.where((name) => name.startsWith('obj/')), isEmpty);
  });

  test('broad compatibility range records the exact SDK game build', () async {
    writeManifest({'supportedGameVersionRange': '>=0.0.2200 <0.0.2300'});
    File(p.join(projectDir.path, 'Sample.dll')).writeAsStringSync('payload');

    final packagePath = await repository.packModDirectory(projectDir.path);
    final archive = readPackage(packagePath);
    final manifestFile = archive.files.singleWhere(
      (file) => file.name == 'topiaforge.mod.json',
    );
    final manifest =
        jsonDecode(utf8.decode(manifestFile.content as List<int>))
            as Map<String, Object?>;
    final builtWith = manifest['builtWith'] as Map<String, Object?>;

    expect(builtWith['gameVersion'], TopiaForgeRuntimeVersions.gameVersion);
    expect(
      builtWith['gameVersion'],
      isNot(manifest['supportedGameVersionRange']),
    );
  });

  test('ships the repo-root game bindings file when present', () async {
    writeManifest();
    File(p.join(projectDir.path, 'Sample.dll')).writeAsStringSync('payload');
    File(
      p.join(repoRoot.path, 'bindings', 'sample.mod.gamebindings.json'),
    ).createSync(recursive: true);

    final packagePath = await repository.packModDirectory(projectDir.path);

    final names = readPackage(packagePath).files.map((f) => f.name).toSet();
    expect(names, contains('bindings/sample.mod.gamebindings.json'));
  });

  test('rejects a manifest missing required fields', () async {
    writeManifest({'entryType': ''});
    await expectLater(
      repository.packModDirectory(projectDir.path),
      throwsA(isA<StateError>()),
    );
  });

  test('rejects a manifest with schemaVersion 2', () async {
    writeManifest({'schemaVersion': 2});
    await expectLater(
      repository.packModDirectory(projectDir.path),
      throwsA(predicate((error) => error.toString().contains('schemaVersion'))),
    );
  });

  test('rejects a manifest with a retired ecosystem id', () async {
    writeManifest({
      'name':
          'robo'
          'topia.old',
    });
    await expectLater(
      repository.packModDirectory(projectDir.path),
      throwsA(predicate((error) => error.toString().contains('name must be'))),
    );
  });

  test('rejects an oversized manifest before decoding it', () async {
    File(
      p.join(projectDir.path, 'topiaforge.mod.json'),
    ).writeAsBytesSync(List<int>.filled(1024 * 1024 + 1, 0x20));

    await expectLater(
      repository.packModDirectory(projectDir.path),
      throwsA(predicate((error) => error.toString().contains('1 MB limit'))),
    );
  });

  test('rejects linked project content without producing a package', () async {
    if (Platform.isWindows) return;
    writeManifest();
    final outside = File(p.join(root.path, 'outside.txt'))
      ..writeAsStringSync('secret');
    Link(p.join(projectDir.path, 'linked.txt')).createSync(outside.path);

    await expectLater(
      repository.packModDirectory(projectDir.path),
      throwsA(predicate((error) => error.toString().contains('symlink'))),
    );
    expect(Directory(p.join(projectDir.path, 'dist')).existsSync(), isFalse);
  });

  test(
    'rejects duplicate package paths instead of silently shadowing',
    () async {
      writeManifest();
      File(p.join(projectDir.path, 'Sample.dll')).writeAsStringSync('payload');
      File(p.join(projectDir.path, 'bindings', 'sample.mod.gamebindings.json'))
        ..createSync(recursive: true)
        ..writeAsStringSync('{}');
      File(p.join(repoRoot.path, 'bindings', 'sample.mod.gamebindings.json'))
        ..createSync(recursive: true)
        ..writeAsStringSync('{}');

      await expectLater(
        repository.packModDirectory(projectDir.path),
        throwsA(
          predicate((error) => error.toString().contains('duplicate path')),
        ),
      );
    },
  );

  test('rejects unsafe characters in the package id', () async {
    writeManifest({'name': 'weird name!', 'version': '1.0.0+build'});
    File(p.join(projectDir.path, 'Sample.dll')).writeAsStringSync('payload');

    await expectLater(
      repository.packModDirectory(projectDir.path),
      throwsA(isA<StateError>()),
    );
  });

  test('produces deterministic archives and omits placeholder files', () async {
    writeManifest();
    File(p.join(projectDir.path, 'z.txt')).writeAsStringSync('z');
    File(p.join(projectDir.path, 'a.txt')).writeAsStringSync('a');
    File(
      p.join(projectDir.path, 'AssetBundles', '.gitkeep'),
    ).createSync(recursive: true);

    final first = await repository.packModDirectory(
      projectDir.path,
      outputDir: p.join(root.path, 'first'),
    );
    final second = await repository.packModDirectory(
      projectDir.path,
      outputDir: p.join(root.path, 'second'),
    );

    expect(File(first).readAsBytesSync(), File(second).readAsBytesSync());
    final archive = readPackage(first);
    expect(
      archive.files.map((file) => file.name),
      orderedEquals(['a.txt', 'topiaforge.mod.json', 'z.txt']),
    );
    expect(archive.files.map((file) => file.lastModTime).toSet(), hasLength(1));
  });

  test('ships build notices and stages an entry API assembly once', () async {
    writeManifest({
      'apiAssemblies': ['Sample.dll'],
    });
    File(p.join(projectDir.path, 'Sample.csproj')).writeAsStringSync('''
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <AssemblyName>Sample</AssemblyName>
  </PropertyGroup>
  <ItemGroup>
    <None Include="notices/LICENSE.txt"
          Link="third_party/vendor/LICENSE.txt"
          CopyToOutputDirectory="PreserveNewest" />
  </ItemGroup>
</Project>
''');
    File(p.join(projectDir.path, 'notices', 'LICENSE.txt'))
      ..createSync(recursive: true)
      ..writeAsStringSync('Redistribution license.');
    File(
      p.join(projectDir.path, 'LICENSE.md'),
    ).writeAsStringSync('Mod license.');

    final packagePath = await repository.packModDirectory(projectDir.path);

    final archive = readPackage(packagePath);
    expect(
      archive.files.where((file) => file.name == 'Sample.dll'),
      hasLength(1),
    );
    final notice = archive.files.singleWhere(
      (file) => file.name == 'third_party/vendor/LICENSE.txt',
    );
    expect(utf8.decode(notice.content as List<int>), 'Redistribution license.');
    final rootLicense = archive.files.singleWhere(
      (file) => file.name == 'LICENSE.md',
    );
    expect(utf8.decode(rootLicense.content as List<int>), 'Mod license.');
  });

  test('build invokes the SDK executable validated for the repo', () async {
    if (Platform.isWindows) return;
    writeManifest();
    File(p.join(projectDir.path, 'Sample.csproj')).writeAsStringSync('''
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
</Project>
''');
    final log = File(p.join(root.path, 'dotnet-invocation.txt'));
    final output = File(
      p.join(projectDir.path, 'bin', 'Release', 'net10.0', 'Sample.dll'),
    );
    final fakeDotnet = File(p.join(root.path, 'validated-dotnet'))
      ..writeAsStringSync('''#!/bin/sh
set -eu
printf '%s\\n' "\$PWD" > ${_shellQuote(log.path)}
printf '%s\\n' "\$@" >> ${_shellQuote(log.path)}
mkdir -p ${_shellQuote(output.parent.path)}
printf 'dll' > ${_shellQuote(output.path)}
''');
    final chmod = await Process.run('chmod', ['700', fakeDotnet.path]);
    expect(chmod.exitCode, 0);
    repository = LocalDeveloperRepository(
      dataRoot: p.join(root.path, 'data'),
      repositoryRoot: repoRoot.path,
      dotnetSdkResolver: (resolvedRoot) async {
        expect(resolvedRoot.absolute.path, repoRoot.absolute.path);
        return DotnetSdkSelection(
          executable: fakeDotnet.path,
          version: '10.0.301',
          requiredVersion: '10.0.301',
        );
      },
    );

    await repository.packModDirectory(projectDir.path);

    final invocation = log.readAsLinesSync();
    expect(
      Directory(invocation.first).resolveSymbolicLinksSync(),
      repoRoot.resolveSymbolicLinksSync(),
    );
    expect(invocation.skip(1), [
      'build',
      p.join(projectDir.absolute.path, 'Sample.csproj'),
      '-c',
      'Release',
    ]);
  });

  test('compiled packages exclude every loader-owned SDK assembly', () async {
    if (Platform.isWindows) return;
    writeManifest();
    File(p.join(projectDir.path, 'Sample.csproj')).writeAsStringSync('''
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><TargetFramework>netstandard2.1</TargetFramework></PropertyGroup>
</Project>
''');
    final output = Directory(
      p.join(projectDir.path, 'bin', 'Release', 'netstandard2.1'),
    );
    final fakeDotnet = File(p.join(root.path, 'validated-dotnet'))
      ..writeAsStringSync('''#!/bin/sh
set -eu
mkdir -p ${_shellQuote(output.path)}
printf mod > ${_shellQuote(p.join(output.path, 'Sample.dll'))}
printf private > ${_shellQuote(p.join(output.path, 'Private.Dependency.dll'))}
for name in Abstractions Analyzers Chronos Interop.Unity Multiplayer Prompts RobotKit Testing Ugc UnityUi Worlds; do
  printf sdk > ${_shellQuote(output.path)}/TopiaForge.Mods.\$name.dll
  printf pdb > ${_shellQuote(output.path)}/TopiaForge.Mods.\$name.pdb
done
''');
    final chmod = await Process.run('chmod', ['700', fakeDotnet.path]);
    expect(chmod.exitCode, 0);
    repository = LocalDeveloperRepository(
      dataRoot: p.join(root.path, 'data'),
      repositoryRoot: repoRoot.path,
      dotnetSdkResolver: (_) async => DotnetSdkSelection(
        executable: fakeDotnet.path,
        version: '10.0.301',
        requiredVersion: '10.0.301',
      ),
    );

    final packagePath = await repository.packModDirectory(projectDir.path);
    final names = readPackage(
      packagePath,
    ).files.map((file) => file.name).toSet();
    expect(names, containsAll(['Sample.dll', 'Private.Dependency.dll']));
    expect(names.where((name) => name.startsWith('TopiaForge.Mods.')), isEmpty);
  });
}

String _shellQuote(String value) => "'${value.replaceAll("'", "'\\''")}'";
