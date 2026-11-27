import 'dart:convert';
import 'dart:io';

import 'package:launcher_data/launcher_data.dart';
import 'package:launcher_domain/launcher_domain.dart';
import 'package:path/path.dart' as p;
import 'package:test/test.dart';

const _sdkPackages = [
  'TopiaForge.Mods.Abstractions',
  'TopiaForge.Mods.Analyzers',
];

/// Scaffolding pins the SDK items in a template's project file to the SDK
/// version and leaves every other item alone. Resolving pins them again, so
/// it must leave the file as scaffolding wrote it.
void main() {
  test('edits only the SDK items, not the items after them', () async {
    // Each self-closing SDK item has no Version and is followed by an element
    // item that has one. The SDK items used to run on to that item's closing
    // tag, so the element item took the SDK version, and StyleCop's
    // PrivateAssets hid the analyzer's missing one.
    final project = await _scaffold('''
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>netstandard2.1</TargetFramework>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="TopiaForge.Mods.Abstractions" />
    <PackageReference Include="Newtonsoft.Json" Version="13.0.3">
      <IncludeAssets>compile</IncludeAssets>
    </PackageReference>
    <PackageReference Include="TopiaForge.Mods.Analyzers" />
    <PackageReference Include="StyleCop.Analyzers" Version="1.1.118" PrivateAssets="all">
      <IncludeAssets>analyzers</IncludeAssets>
    </PackageReference>
  </ItemGroup>
</Project>
''');

    expect(
      project,
      contains('''
  <ItemGroup>
    <PackageReference Include="TopiaForge.Mods.Abstractions" Version="1.0.0" />
    <PackageReference Include="Newtonsoft.Json" Version="13.0.3">
      <IncludeAssets>compile</IncludeAssets>
    </PackageReference>
    <PackageReference Include="TopiaForge.Mods.Analyzers" Version="1.0.0" PrivateAssets="all" />
    <PackageReference Include="StyleCop.Analyzers" Version="1.1.118" PrivateAssets="all">
      <IncludeAssets>analyzers</IncludeAssets>
    </PackageReference>
  </ItemGroup>
'''),
    );
  });

  test('adds attributes after the others, wherever lines break', () async {
    // Attributes used to go in front of Include, after a literal
    // `<PackageReference `. With a line break there, the item got no
    // Version at all. With a space, Include was no longer first, so the next
    // rewrite did not find the item and added a second copy of it.
    final project = await _scaffold('''
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>netstandard2.1</TargetFramework>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference
      Include="TopiaForge.Mods.Abstractions" />
    <PackageReference
      Include="TopiaForge.Mods.Analyzers"
      GeneratePathProperty="true"
      />
  </ItemGroup>
</Project>
''');

    expect(
      project,
      contains('''
  <ItemGroup>
    <PackageReference
      Include="TopiaForge.Mods.Abstractions" Version="1.0.0" />
    <PackageReference
      Include="TopiaForge.Mods.Analyzers"
      GeneratePathProperty="true" Version="1.0.0" PrivateAssets="all"
      />
  </ItemGroup>
'''),
    );
  });

  test('reads attributes in any case and either quote', () async {
    // MSBuild matches metadata names ignoring case, and the later of two
    // attributes wins, so an added Version lost to a lowercase one. XML
    // forbids the same attribute twice, so an added Version next to a
    // single-quoted one stopped the project from loading.
    final project = await _scaffold('''
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>netstandard2.1</TargetFramework>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="TopiaForge.Mods.Abstractions" version="0.0.1" />
    <PackageReference Include='TopiaForge.Mods.Analyzers' Version='0.0.1' privateassets='all' />
  </ItemGroup>
</Project>
''');

    expect(
      project,
      contains('''
  <ItemGroup>
    <PackageReference Include="TopiaForge.Mods.Abstractions" Version="1.0.0" />
    <PackageReference Include='TopiaForge.Mods.Analyzers' Version="1.0.0" privateassets='all' />
  </ItemGroup>
'''),
    );
  });

  test('reads only the start tag, however it is spaced', () async {
    // White space may surround `=`, and one Version stays of several. The
    // comments set nothing, but the checks used to read them: the pin went
    // into the first comment and the analyzer kept no PrivateAssets.
    final project = await _scaffold('''
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>netstandard2.1</TargetFramework>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="TopiaForge.Mods.Abstractions" Version = "0.0.1" VERSION="0.0.2">
      <!-- Version="0.0.3" -->
    </PackageReference>
    <PackageReference Include="TopiaForge.Mods.Analyzers">
      <!-- PrivateAssets="all" -->
    </PackageReference>
  </ItemGroup>
</Project>
''');

    expect(
      project,
      contains('''
  <ItemGroup>
    <PackageReference Include="TopiaForge.Mods.Abstractions" Version="1.0.0">
      <!-- Version="0.0.3" -->
    </PackageReference>
    <PackageReference Include="TopiaForge.Mods.Analyzers" Version="1.0.0" PrivateAssets="all">
      <!-- PrivateAssets="all" -->
    </PackageReference>
  </ItemGroup>
'''),
    );
  });

  test('replaces a Version child with the pinned attribute', () async {
    // MSBuild applies child metadata after the attributes, so either child
    // would override a Version attribute added or set next to it. The
    // PrivateAssets child is already set, so no attribute competes with it.
    final project = await _scaffold('''
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>netstandard2.1</TargetFramework>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="TopiaForge.Mods.Abstractions">
      <Version>0.0.1</Version>
    </PackageReference>
    <PackageReference Include="TopiaForge.Mods.Analyzers" Version="0.0.1">
      <PrivateAssets>all</PrivateAssets>
      <Version>0.0.2</Version>
    </PackageReference>
  </ItemGroup>
</Project>
''');

    expect(
      project,
      contains('''
  <ItemGroup>
    <PackageReference Include="TopiaForge.Mods.Abstractions" Version="1.0.0">
    </PackageReference>
    <PackageReference Include="TopiaForge.Mods.Analyzers" Version="1.0.0">
      <PrivateAssets>all</PrivateAssets>
    </PackageReference>
  </ItemGroup>
'''),
    );
  });

  test('reads metadata children the way MSBuild does', () async {
    // MSBuild matches metadata names ignoring case, a child's condition may
    // hold, and an empty child clears the version, so each Version child
    // goes. A child inside a comment sets nothing, so it stays, and the
    // analyzer still gets PrivateAssets.
    final project = await _scaffold(r'''
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>netstandard2.1</TargetFramework>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="TopiaForge.Mods.Abstractions">
      <!-- <Version>0.0.1</Version> -->
      <version>0.0.2</version>
      <Version Condition="'$(Configuration)' == 'Debug'">0.0.3</Version>
      <IncludeAssets>compile</IncludeAssets>
      <Version />
    </PackageReference>
    <PackageReference Include="TopiaForge.Mods.Analyzers">
      <!-- <PrivateAssets>all</PrivateAssets> -->
      <IncludeAssets>analyzers</IncludeAssets>
    </PackageReference>
  </ItemGroup>
</Project>
''');

    expect(
      project,
      contains('''
  <ItemGroup>
    <PackageReference Include="TopiaForge.Mods.Abstractions" Version="1.0.0">
      <!-- <Version>0.0.1</Version> -->
      <IncludeAssets>compile</IncludeAssets>
    </PackageReference>
    <PackageReference Include="TopiaForge.Mods.Analyzers" Version="1.0.0" PrivateAssets="all">
      <!-- <PrivateAssets>all</PrivateAssets> -->
      <IncludeAssets>analyzers</IncludeAssets>
    </PackageReference>
  </ItemGroup>
'''),
    );
  });

  test('pins an item wherever Include sits, however its id is cased', () async {
    // NuGet matches ids ignoring case, and MSBuild trims the white space
    // around one. The finder used to need `Include="id"` first, in the id's
    // exact case, so it missed both items and added unconditional copies.
    // Each is now pinned in place, keeping its condition and its spelling.
    final project = await _scaffold(r'''
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>netstandard2.1</TargetFramework>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Version="0.0.1" Include = " topiaforge.mods.abstractions " />
    <PackageReference Condition="'$(Configuration)' != 'Fuzz'" Include="TopiaForge.Mods.Analyzers" />
  </ItemGroup>
</Project>
''');

    expect(
      project,
      contains(r'''
  <ItemGroup>
    <PackageReference Version="1.0.0" Include = " topiaforge.mods.abstractions " />
    <PackageReference Condition="'$(Configuration)' != 'Fuzz'" Include="TopiaForge.Mods.Analyzers" Version="1.0.0" PrivateAssets="all" />
  </ItemGroup>
'''),
    );
  });

  test('reads each item to its own end, outside comments', () async {
    // MSBuild ignores comments and reads item types in any case. The finder
    // used to pin the commented-out item, miss the lowercase one, end the
    // analyzer at the end tag in its comment, which left its Version child
    // to beat the pin, and count the commented-out import as an import.
    final project = await _scaffold('''
<Project Sdk="Microsoft.NET.Sdk">
  <!-- <Import Project="topiaforge.dev.props" /> -->
  <PropertyGroup>
    <TargetFramework>netstandard2.1</TargetFramework>
  </PropertyGroup>
  <ItemGroup>
    <!-- <PackageReference Include="TopiaForge.Mods.Abstractions" Version="0.0.1" /> -->
    <packagereference Include="TopiaForge.Mods.Abstractions" Label="a > b">
    </packagereference >
    <PackageReference Include="TopiaForge.Mods.Analyzers" PrivateAssets="all">
      <!-- </PackageReference> -->
      <Version>0.0.1</Version>
    </PackageReference>
  </ItemGroup>
</Project>
''');

    expect(
      project,
      startsWith('''
<Project Sdk="Microsoft.NET.Sdk">
  <Import Project="topiaforge.dev.props" Condition="Exists('topiaforge.dev.props')" />
  <!-- <Import Project="topiaforge.dev.props" /> -->
'''),
    );
    expect(
      project,
      contains('''
  <ItemGroup>
    <!-- <PackageReference Include="TopiaForge.Mods.Abstractions" Version="0.0.1" /> -->
    <packagereference Include="TopiaForge.Mods.Abstractions" Label="a > b" Version="1.0.0">
    </packagereference >
    <PackageReference Include="TopiaForge.Mods.Analyzers" PrivateAssets="all" Version="1.0.0">
      <!-- </PackageReference> -->
    </PackageReference>
  </ItemGroup>
'''),
    );
  });

  test('keeps later Update and Remove items from undoing the pin', () async {
    // MSBuild applies an Update or Remove item to the items before it, so
    // the Update used to set Abstractions back to 0.0.1, and Debug builds
    // lost the analyzer.
    final project = await _scaffold(r'''
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>netstandard2.1</TargetFramework>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="TopiaForge.Mods.Abstractions" Version="1.0.0" />
    <PackageReference Include="TopiaForge.Mods.Analyzers" Version="1.0.0" PrivateAssets="all" />
    <PackageReference Update="TopiaForge.Mods.Abstractions" Version="0.0.1" />
  </ItemGroup>
  <ItemGroup Condition="'$(Configuration)' == 'Debug'">
    <PackageReference Remove="TopiaForge.Mods.Analyzers" />
  </ItemGroup>
</Project>
''');

    expect(
      project,
      contains(r'''
  <ItemGroup>
    <PackageReference Include="TopiaForge.Mods.Abstractions" Version="1.0.0" />
    <PackageReference Include="TopiaForge.Mods.Analyzers" Version="1.0.0" PrivateAssets="all" />
    <PackageReference Update="TopiaForge.Mods.Abstractions" />
  </ItemGroup>
  <ItemGroup Condition="'$(Configuration)' == 'Debug'">
  </ItemGroup>
'''),
    );
  });

  test('finds the dev props import however it is written', () async {
    // The check used to need `Project="…"` first and double-quoted, so this
    // project got a second import, which MSBuild warns about.
    const import =
        "  <Import Condition=\"Exists('topiaforge.dev.props')\" "
        "Project = 'topiaforge.dev.props' />";
    final project = await _scaffold('''
<Project Sdk="Microsoft.NET.Sdk">
$import
  <PropertyGroup>
    <TargetFramework>netstandard2.1</TargetFramework>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="TopiaForge.Mods.Abstractions" Version="1.0.0" />
    <PackageReference Include="TopiaForge.Mods.Analyzers" Version="1.0.0" PrivateAssets="all" />
  </ItemGroup>
</Project>
''');

    expect(project, startsWith('<Project Sdk="Microsoft.NET.Sdk">\n$import\n'));
    expect('<Import'.allMatches(project), hasLength(1));
  });
}

/// Stands in for the NuGet restore that resolving runs after its rewrite,
/// which needs real SDK packages.
final class _RestoreSkipped implements Exception {
  const _RestoreSkipped();
}

/// Scaffolds a mod from a `pins` template whose only project file is
/// [project], against a packaged SDK 1.0.0, and returns the project file
/// scaffolding wrote.
///
/// Two resolves then rewrite the file again, and each must leave it as it
/// is, with one item for each SDK package outside comments.
Future<String> _scaffold(String project) async {
  final root = Directory.systemTemp.createTempSync('topiaforge-sdk-pins-');
  addTearDown(() => root.deleteSync(recursive: true));
  final source = p.join(root.path, 'source');
  _writeSdkPack(source);
  _writeTemplate(source, project);
  final repository = LocalDeveloperRepository(
    dataRoot: p.join(root.path, 'data'),
    repositoryRoot: source,
    dotnetSdkResolver: (_) async => throw const _RestoreSkipped(),
  );

  final workspace = await repository.createModProject(
    parentDirectory: p.join(root.path, 'mods'),
    id: 'sample.pins',
    name: 'Pins',
    options: const ModScaffoldOptions(template: 'pins'),
  );

  final file = File(p.join(workspace.projectRoot, 'Pins.csproj'));
  final scaffolded = file.readAsStringSync();
  for (final rewrite in const ['second', 'third']) {
    await expectLater(
      repository.resolveDeveloperProject(workspace.projectRoot),
      throwsA(isA<_RestoreSkipped>()),
    );
    expect(file.readAsStringSync(), scaffolded, reason: 'the $rewrite rewrite');
  }
  final items = scaffolded.replaceAll(RegExp('<!--.*?-->', dotAll: true), '');
  for (final package in _sdkPackages) {
    expect(
      RegExp(
        'Include\\s*=\\s*["\']\\s*${RegExp.escape(package)}\\s*["\']',
        caseSensitive: false,
      ).allMatches(items),
      hasLength(1),
      reason: package,
    );
  }
  return scaffolded;
}

/// Writes a packaged SDK 1.0.0 with the contract and analyzer packages.
void _writeSdkPack(String source) {
  final fixture = Directory(p.join(source, 'fixture'))
    ..createSync(recursive: true);
  File file(String name, String content) =>
      File(p.join(fixture.path, name))..writeAsStringSync(content);
  final pack = const SdkReferencePackWriter().write(
    destination: Directory(p.join(source, 'sdk', '1.0.0')),
    sdkVersion: '1.0.0',
    dotnetSdkVersion: '10.0.301',
    references: {'TopiaForge.Mods.Abstractions': file('Sdk.dll', 'sdk')},
    documentation: {'TopiaForge.Mods.Abstractions': file('Sdk.xml', '<doc />')},
    analyzers: {'TopiaForge.Mods.Analyzers': file('Analyzers.dll', 'analyzer')},
  );
  File(p.join(source, 'sdk', 'index.json')).writeAsStringSync(
    jsonEncode({
      'schemaVersion': 1,
      'defaultVersion': '1.0.0',
      'versions': {
        '1.0.0': {'manifestSha256': pack.manifestSha256},
      },
    }),
  );
}

/// Writes the `pins` mod template, whose only project file is [project].
void _writeTemplate(String source, String project) {
  final template = Directory(p.join(source, 'templates', 'mod', 'pins'))
    ..createSync(recursive: true);
  File(
    p.join(template.path, 'template.json'),
  ).writeAsStringSync(jsonEncode({'id': 'pins'}));
  File(p.join(template.path, 'Pins.csproj')).writeAsStringSync(project);
}
