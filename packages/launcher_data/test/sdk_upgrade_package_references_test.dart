import 'dart:convert';
import 'dart:io';

import 'package:launcher_data/launcher_data.dart';
import 'package:launcher_domain/launcher_domain.dart';
import 'package:path/path.dart' as p;
import 'package:test/test.dart';

/// The contract and analyzer packages in each fixture SDK. A feed carries
/// one version of each: its own.
const _contracts = [
  'TopiaForge.Mods.Abstractions',
  'TopiaForge.Mods.Interop.Unity',
  'TopiaForge.Mods.Multiplayer',
  'TopiaForge.Mods.RobotKit',
  'TopiaForge.Mods.Testing',
];
const _analyzers = [
  'TopiaForge.Mods.Analyzers',
  'TopiaForge.Mods.Multiplayer.Generators',
];

/// Adopting a newer SDK must move every SDK item a project has to that SDK,
/// since its feed carries no other version. Items the manifest's
/// dependencies do not imply used to stay on the old SDK: the multiplayer
/// generator and the Unity interop contracts that `topiaforge mod add`
/// writes, and a module the mod depends on only optionally.
///
/// Restoring also gives a test project that compiles the mod's own sources
/// the multiplayer generator the mod project has, since those sources carry
/// the mod's contract.
void main() {
  test('adopting a newer SDK re-pins every SDK item a project has', () async {
    final root = Directory.systemTemp.createTempSync('topiaforge-sdk-upgrade-');
    addTearDown(() => root.deleteSync(recursive: true));
    final source = p.join(root.path, 'source');
    final installed = {'1.0.0': _writeSdkPack(source, '1.0.0')};
    _writeSdkIndex(source, installed, defaultVersion: '1.0.0');
    _writeTemplate(source);
    final repository = LocalDeveloperRepository(
      dataRoot: p.join(root.path, 'data'),
      repositoryRoot: source,
      dotnetSdkResolver: (_) async => throw const _RestoreSkipped(),
    );
    final workspace = await repository.createModProject(
      parentDirectory: p.join(root.path, 'mods'),
      id: 'sample.upgrade',
      name: 'Upgrade',
      options: const ModScaffoldOptions(template: 'upgrade'),
    );
    final project = File(p.join(workspace.projectRoot, 'Upgrade.csproj'));
    final tests = File(
      p.join(
        workspace.projectRoot,
        'tests',
        'Upgrade.Tests',
        'Upgrade.Tests.csproj',
      ),
    );
    Map<String, String> entryItems(String version) => {
      'TopiaForge.Mods.Abstractions': version,
      'TopiaForge.Mods.Analyzers': version,
      'TopiaForge.Mods.Multiplayer': version,
      'TopiaForge.Mods.Multiplayer.Generators': version,
      'TopiaForge.Mods.RobotKit': version,
      'TopiaForge.Mods.Interop.Unity': version,
      'Newtonsoft.Json': '13.0.3',
    };
    Map<String, String> testItems(String version) => {
      'NUnit': '4.3.2',
      'TopiaForge.Mods.Abstractions': version,
      'TopiaForge.Mods.Multiplayer': version,
      'TopiaForge.Mods.RobotKit': version,
      'TopiaForge.Mods.Testing': version,
    };
    expect(_versions(project), entryItems('1.0.0'));
    expect(_versions(tests), testItems('1.0.0'));

    // Install a newer SDK and adopt it as the restore error advises: delete
    // the project's SDK lock and restore.
    installed['2.0.0'] = _writeSdkPack(source, '2.0.0');
    _writeSdkIndex(source, installed, defaultVersion: '2.0.0');
    File(
      p.join(workspace.projectRoot, 'topiaforge.sdk.lock.json'),
    ).deleteSync();
    await expectLater(
      repository.resolveDeveloperProject(workspace.projectRoot),
      throwsA(isA<_RestoreSkipped>()),
    );

    final lock =
        jsonDecode(
              File(
                p.join(workspace.projectRoot, 'topiaforge.sdk.lock.json'),
              ).readAsStringSync(),
            )
            as Map<String, Object?>;
    expect(lock['sdkVersion'], '2.0.0');
    expect(_versions(project), entryItems('2.0.0'));
    // The test project gains no analyzer or generator: only the items it
    // has move, with the ones every test project needs.
    expect(_versions(tests), testItems('2.0.0'));
  });

  test(
    'a test project that compiles the mod sources gets the generator',
    () async {
      final root = Directory.systemTemp.createTempSync(
        'topiaforge-sdk-sources-',
      );
      addTearDown(() => root.deleteSync(recursive: true));
      final source = p.join(root.path, 'source');
      final installed = {'1.0.0': _writeSdkPack(source, '1.0.0')};
      _writeSdkIndex(source, installed, defaultVersion: '1.0.0');
      _writeSourcesTemplate(source);
      final repository = LocalDeveloperRepository(
        dataRoot: p.join(root.path, 'data'),
        repositoryRoot: source,
        dotnetSdkResolver: (_) async => throw const _RestoreSkipped(),
      );
      final workspace = await repository.createModProject(
        parentDirectory: p.join(root.path, 'mods'),
        id: 'sample.sources',
        name: 'Sources',
        options: const ModScaffoldOptions(template: 'sources'),
      );
      final project = File(p.join(workspace.projectRoot, 'Sources.csproj'));
      File testProject(String name) =>
          File(p.join(workspace.projectRoot, 'tests', name, '$name.csproj'));
      final compiling = testProject('Sources.Tests');
      final referencing = testProject('Sources.Reference.Tests');
      const testItems = {
        'NUnit': '4.3.2',
        'TopiaForge.Mods.Abstractions': '1.0.0',
        'TopiaForge.Mods.Testing': '1.0.0',
      };
      // Without multiplayer, no test project needs the generator.
      expect(_versions(compiling), testItems);
      expect(_versions(referencing), testItems);

      // `topiaforge mod add multiplayer` gives the mod project the contract
      // and its generator, and the manifest the dependency; then the author
      // restores.
      project.writeAsStringSync(
        withPinnedSdkPackageReferences(
          project.readAsStringSync(),
          packages: const [
            'TopiaForge.Mods.Multiplayer',
            'TopiaForge.Mods.Multiplayer.Generators',
          ],
          version: '1.0.0',
        ),
      );
      final manifest = File(
        p.join(workspace.projectRoot, 'topiaforge.mod.json'),
      );
      final map =
          jsonDecode(manifest.readAsStringSync()) as Map<String, Object?>;
      map['dependencies'] = {
        ...?(map['dependencies'] as Map?)?.cast<String, Object?>(),
        'io.github.furroxide.topiaforge.multiplayer': '1.0.0',
      };
      manifest.writeAsStringSync(jsonEncode(map));
      await expectLater(
        repository.resolveDeveloperProject(workspace.projectRoot),
        throwsA(isA<_RestoreSkipped>()),
      );

      // The test project that compiles the mod's sources compiles its
      // contract too, so it gets the generator that writes the contract's
      // other half. The one that references the built mod has that half.
      expect(_versions(compiling), {
        ...testItems,
        'TopiaForge.Mods.Multiplayer': '1.0.0',
        'TopiaForge.Mods.Multiplayer.Generators': '1.0.0',
      });
      expect(
        compiling.readAsStringSync(),
        contains(
          '<PackageReference Include="TopiaForge.Mods.Multiplayer.Generators" '
          'Version="1.0.0" PrivateAssets="all" />',
        ),
      );
      expect(_versions(referencing), {
        ...testItems,
        'TopiaForge.Mods.Multiplayer': '1.0.0',
      });
    },
  );
}

/// Stands in for the NuGet restore that resolving runs after its rewrite,
/// which needs real SDK packages.
final class _RestoreSkipped implements Exception {
  const _RestoreSkipped();
}

/// The Version of each `PackageReference` item in [project], by id, in the
/// form scaffolding and the pin write.
Map<String, String> _versions(File project) {
  final versions = <String, String>{};
  for (final match in RegExp(
    r'<PackageReference Include="([^"]+)" Version="([^"]+)"',
  ).allMatches(project.readAsStringSync())) {
    final id = match.group(1)!;
    expect(versions.containsKey(id), isFalse, reason: '$id has one item');
    versions[id] = match.group(2)!;
  }
  return versions;
}

/// Writes a packaged SDK [version] carrying every package in [_contracts]
/// and [_analyzers], and returns its manifest digest.
String _writeSdkPack(String source, String version) {
  final fixture = Directory(p.join(source, 'fixture', version));
  File file(String name, String content) => File(p.join(fixture.path, name))
    ..parent.createSync(recursive: true)
    ..writeAsStringSync(content);
  final pack = const SdkReferencePackWriter().write(
    destination: Directory(p.join(source, 'sdk', version)),
    sdkVersion: version,
    dotnetSdkVersion: '10.0.301',
    references: {
      for (final id in _contracts) id: file('$id.dll', 'reference $id'),
    },
    documentation: {
      for (final id in _contracts) id: file('$id.xml', '<doc />'),
    },
    analyzers: {for (final id in _analyzers) id: file('$id.dll', id)},
    runtimeAssemblies: {
      'TopiaForge.Mods.Testing': file(
        'runtime/TopiaForge.Mods.Testing.dll',
        'runtime',
      ),
    },
  );
  return pack.manifestSha256;
}

/// Writes the packaged SDK index for the [installed] digests by version.
void _writeSdkIndex(
  String source,
  Map<String, String> installed, {
  required String defaultVersion,
}) {
  File(p.join(source, 'sdk', 'index.json')).writeAsStringSync(
    jsonEncode({
      'schemaVersion': 1,
      'defaultVersion': defaultVersion,
      'versions': {
        for (final entry in installed.entries)
          entry.key: {'manifestSha256': entry.value},
      },
    }),
  );
}

/// Writes the `upgrade` mod template: a multiplayer mod with RobotKit as an
/// optional dependency and the Unity interop contracts, as `topiaforge mod
/// add` leaves them, and a test project that also covers RobotKit.
void _writeTemplate(String source) {
  final template = Directory(p.join(source, 'templates', 'mod', 'upgrade'))
    ..createSync(recursive: true);
  File(p.join(template.path, 'template.json')).writeAsStringSync(
    jsonEncode({
      'id': 'upgrade',
      'manifestDefaults': {
        'dependencies': {'io.github.furroxide.topiaforge.multiplayer': '1.0.0'},
        'optionalDependencies': {
          'io.github.furroxide.topiaforge.robotkit': '>=1.0.0 <2.0.0',
        },
      },
    }),
  );
  File(p.join(template.path, 'Upgrade.csproj')).writeAsStringSync('''
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>netstandard2.1</TargetFramework>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="TopiaForge.Mods.Abstractions" Version="1.0.0" />
    <PackageReference Include="TopiaForge.Mods.Analyzers" Version="1.0.0" PrivateAssets="all" />
    <PackageReference Include="TopiaForge.Mods.Multiplayer" Version="1.0.0" />
    <PackageReference Include="TopiaForge.Mods.Multiplayer.Generators" Version="1.0.0" PrivateAssets="all" />
    <PackageReference Include="TopiaForge.Mods.RobotKit" Version="1.0.0" />
    <PackageReference Include="TopiaForge.Mods.Interop.Unity" Version="1.0.0" />
    <PackageReference Include="Newtonsoft.Json" Version="13.0.3" />
  </ItemGroup>
</Project>
''');
  final tests = Directory(p.join(template.path, 'tests', 'Upgrade.Tests'))
    ..createSync(recursive: true);
  File(p.join(tests.path, 'Upgrade.Tests.csproj')).writeAsStringSync('''
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="NUnit" Version="4.3.2" />
    <PackageReference Include="TopiaForge.Mods.Abstractions" Version="1.0.0" />
    <PackageReference Include="TopiaForge.Mods.Multiplayer" Version="1.0.0" />
    <PackageReference Include="TopiaForge.Mods.RobotKit" Version="1.0.0" />
    <PackageReference Include="TopiaForge.Mods.Testing" Version="1.0.0" />
  </ItemGroup>
</Project>
''');
}

/// Writes the `sources` mod template: a mod with no module yet and two test
/// projects, one that compiles the mod's own sources, as the single-project
/// templates' tests do, and one that references the built mod, as the
/// multiplayer template's tests do, and compiles only files of its own.
void _writeSourcesTemplate(String source) {
  final template = Directory(p.join(source, 'templates', 'mod', 'sources'))
    ..createSync(recursive: true);
  File(p.join(template.path, 'template.json')).writeAsStringSync(
    jsonEncode({'id': 'sources', 'manifestDefaults': <String, Object?>{}}),
  );
  File(p.join(template.path, 'Sources.csproj')).writeAsStringSync('''
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>netstandard2.1</TargetFramework>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="TopiaForge.Mods.Abstractions" Version="1.0.0" />
    <PackageReference Include="TopiaForge.Mods.Analyzers" Version="1.0.0" PrivateAssets="all" />
  </ItemGroup>
  <ItemGroup>
    <Compile Remove="tests\\**\\*.cs" />
  </ItemGroup>
</Project>
''');
  void writeTests(String name, String sources) {
    final tests = Directory(p.join(template.path, 'tests', name))
      ..createSync(recursive: true);
    File(p.join(tests.path, '$name.csproj')).writeAsStringSync('''
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
  </PropertyGroup>
  <ItemGroup>
    $sources
  </ItemGroup>
  <ItemGroup>
    <PackageReference Include="NUnit" Version="4.3.2" />
    <PackageReference Include="TopiaForge.Mods.Abstractions" Version="1.0.0" />
    <PackageReference Include="TopiaForge.Mods.Testing" Version="1.0.0" />
  </ItemGroup>
</Project>
''');
  }

  writeTests(
    'Sources.Tests',
    r'<Compile Include="..\..\*.cs" Link="ModSources\%(Filename)%(Extension)" />',
  );
  writeTests(
    'Sources.Reference.Tests',
    r'<ProjectReference Include="..\..\Sources.csproj" /><Compile Include="Fixtures\*.cs" />',
  );
}
