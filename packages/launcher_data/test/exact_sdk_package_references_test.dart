import 'dart:convert';
import 'dart:io';

import 'package:launcher_data/launcher_data.dart';
import 'package:launcher_domain/launcher_domain.dart';
import 'package:path/path.dart' as p;
import 'package:test/test.dart';

/// Scaffolding pins the SDK items in a template's project file to the SDK
/// version and leaves every other item alone.
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
    <PackageReference Version="1.0.0" Include="TopiaForge.Mods.Abstractions" />
    <PackageReference Include="Newtonsoft.Json" Version="13.0.3">
      <IncludeAssets>compile</IncludeAssets>
    </PackageReference>
    <PackageReference PrivateAssets="all" Version="1.0.0" Include="TopiaForge.Mods.Analyzers" />
    <PackageReference Include="StyleCop.Analyzers" Version="1.1.118" PrivateAssets="all">
      <IncludeAssets>analyzers</IncludeAssets>
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
    <PackageReference Version="1.0.0" Include="TopiaForge.Mods.Abstractions">
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
    <PackageReference Version="1.0.0" Include="TopiaForge.Mods.Abstractions">
      <!-- <Version>0.0.1</Version> -->
      <IncludeAssets>compile</IncludeAssets>
    </PackageReference>
    <PackageReference PrivateAssets="all" Version="1.0.0" Include="TopiaForge.Mods.Analyzers">
      <!-- <PrivateAssets>all</PrivateAssets> -->
      <IncludeAssets>analyzers</IncludeAssets>
    </PackageReference>
  </ItemGroup>
'''),
    );
  });
}

/// Scaffolds a mod from a `pins` template whose only project file is
/// [project], against a packaged SDK 1.0.0, and returns the project file
/// scaffolding wrote.
Future<String> _scaffold(String project) async {
  final root = Directory.systemTemp.createTempSync('topiaforge-sdk-pins-');
  addTearDown(() => root.deleteSync(recursive: true));
  final source = p.join(root.path, 'source');
  _writeSdkPack(source);
  _writeTemplate(source, project);
  final repository = LocalDeveloperRepository(
    dataRoot: p.join(root.path, 'data'),
    repositoryRoot: source,
  );

  final workspace = await repository.createModProject(
    parentDirectory: p.join(root.path, 'mods'),
    id: 'sample.pins',
    name: 'Pins',
    options: const ModScaffoldOptions(template: 'pins'),
  );

  return File(p.join(workspace.projectRoot, 'Pins.csproj')).readAsStringSync();
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
