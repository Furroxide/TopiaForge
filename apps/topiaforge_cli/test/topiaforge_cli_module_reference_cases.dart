part of 'topiaforge_cli_test.dart';

/// `mod add|remove <module>` edits only the module's own PackageReference
/// items, the way MSBuild reads them.
void _moduleReferenceCliTests(_CliTestHarness Function() currentHarness) {
  test('mod add pins only the module items, and remove finds them', () async {
    // Each self-closing SDK item used to run on to the next
    // `</PackageReference>`, so Newtonsoft.Json took the SDK version, and
    // StyleCop's PrivateAssets hid the generator's missing one.
    final project = await _scaffoldModProject(currentHarness(), 't.run-on', '''
<Project Sdk="Microsoft.NET.Sdk">
  <ItemGroup>
    <PackageReference Include="TopiaForge.Mods.Multiplayer" />
    <PackageReference Include="Newtonsoft.Json" Version="13.0.3">
      <IncludeAssets>compile</IncludeAssets>
    </PackageReference>
    <PackageReference Include="TopiaForge.Mods.Multiplayer.Generators" Version="0.0.1" />
    <PackageReference Include="StyleCop.Analyzers" Version="1.1.118" PrivateAssets="all">
      <IncludeAssets>analyzers</IncludeAssets>
    </PackageReference>
  </ItemGroup>
</Project>
''');

    await _runModule(currentHarness(), 'add', 'multiplayer', project);

    const multiplayer =
        '<PackageReference Include="TopiaForge.Mods.Multiplayer" Version="0.2.0" />';
    const generators =
        '<PackageReference Include="TopiaForge.Mods.Multiplayer.Generators" Version="0.2.0" PrivateAssets="all" />';
    const pinned =
        '''
<Project Sdk="Microsoft.NET.Sdk">
  <ItemGroup>
    $multiplayer
    <PackageReference Include="Newtonsoft.Json" Version="13.0.3">
      <IncludeAssets>compile</IncludeAssets>
    </PackageReference>
    $generators
    <PackageReference Include="StyleCop.Analyzers" Version="1.1.118" PrivateAssets="all">
      <IncludeAssets>analyzers</IncludeAssets>
    </PackageReference>
  </ItemGroup>
</Project>
''';
    expect(project.readAsStringSync(), pinned);

    // The added attributes follow Include, so remove still finds the items.
    await _runModule(currentHarness(), 'remove', 'multiplayer', project);

    expect(
      project.readAsStringSync(),
      pinned.replaceFirst(multiplayer, '').replaceFirst(generators, ''),
    );
  });

  test('mod remove deletes the module item, not the items after it', () async {
    // The run-on match used to delete everything up to Newtonsoft.Json's
    // closing tag, Newtonsoft.Json included.
    const robotKit =
        '<PackageReference Include="TopiaForge.Mods.RobotKit" Version="0.2.0" />';
    const original =
        '''
<Project Sdk="Microsoft.NET.Sdk">
  <ItemGroup>
    $robotKit
    <PackageReference Include="Newtonsoft.Json" Version="13.0.3">
      <IncludeAssets>compile</IncludeAssets>
    </PackageReference>
  </ItemGroup>
</Project>
''';
    final project = await _scaffoldModProject(
      currentHarness(),
      't.remove-run-on',
      original,
    );

    await _runModule(currentHarness(), 'remove', 'robotkit', project);

    expect(project.readAsStringSync(), original.replaceFirst(robotKit, ''));
  });

  test('mod add replaces Version children with the pin', () async {
    // MSBuild applies child metadata after the attributes and matches its
    // name ignoring case, so each Version child, even an empty one, used to
    // override the pinned attribute. The PrivateAssets child is already set,
    // so no attribute competes with it.
    final project = await _scaffoldModProject(
      currentHarness(),
      't.child-metadata',
      '''
<Project Sdk="Microsoft.NET.Sdk">
  <ItemGroup>
    <PackageReference Include="TopiaForge.Mods.Multiplayer" Version="0.0.1">
      <version>0.0.2</version>
      <IncludeAssets>compile</IncludeAssets>
      <Version />
    </PackageReference>
    <PackageReference Include="TopiaForge.Mods.Multiplayer.Generators" Version="0.0.1">
      <PrivateAssets>all</PrivateAssets>
      <Version>0.0.3</Version>
    </PackageReference>
  </ItemGroup>
</Project>
''',
    );

    await _runModule(currentHarness(), 'add', 'multiplayer', project);

    expect(project.readAsStringSync(), '''
<Project Sdk="Microsoft.NET.Sdk">
  <ItemGroup>
    <PackageReference Include="TopiaForge.Mods.Multiplayer" Version="0.2.0">
      <IncludeAssets>compile</IncludeAssets>
    </PackageReference>
    <PackageReference Include="TopiaForge.Mods.Multiplayer.Generators" Version="0.2.0">
      <PrivateAssets>all</PrivateAssets>
    </PackageReference>
  </ItemGroup>
</Project>
''');
  });

  test('mod add and remove match the package id literally', () async {
    // The id was interpolated into a pattern, where each `.` matched any
    // character, so another package's item was pinned and then removed.
    const lookalike =
        '<PackageReference Include="TopiaForge-Mods-RobotKit" Version="2.0.0" />';
    const original =
        '<Project Sdk="Microsoft.NET.Sdk">\n'
        '  <ItemGroup>\n'
        '    $lookalike\n'
        '  </ItemGroup>\n'
        '</Project>\n';
    final project = await _scaffoldModProject(
      currentHarness(),
      't.literal-id',
      original,
    );

    await _runModule(currentHarness(), 'add', 'robotkit', project);
    expect(project.readAsStringSync(), '''
<Project Sdk="Microsoft.NET.Sdk">
  <ItemGroup>
    $lookalike
  </ItemGroup>
  <ItemGroup>
    <PackageReference Include="TopiaForge.Mods.RobotKit" Version="0.2.0" />
  </ItemGroup>
</Project>
''');

    await _runModule(currentHarness(), 'remove', 'robotkit', project);
    final removed = project.readAsStringSync();
    expect(removed, contains(lookalike));
    expect(removed, isNot(contains('"TopiaForge.Mods.RobotKit"')));
  });

  test(
    'mod add keeps later Update and Remove items from undoing the pin',
    () async {
      // MSBuild applies an Update or Remove item to the items before it, so
      // either would leave RobotKit at 0.0.1 or drop it.
      const robotKit = 'TopiaForge.Mods.RobotKit';
      final project = await _scaffoldModProject(
        currentHarness(),
        't.update-remove',
        '<Project Sdk="Microsoft.NET.Sdk">\n'
            '  <ItemGroup>\n'
            '    <PackageReference Include="$robotKit" Version="0.0.1" />\n'
            '    <PackageReference Update="$robotKit" Version="0.0.1" '
            'PrivateAssets="all" />\n'
            '  </ItemGroup>\n'
            '  <ItemGroup>\n'
            '    <PackageReference Remove="$robotKit" />\n'
            '  </ItemGroup>\n'
            '</Project>\n',
      );

      await _runModule(currentHarness(), 'add', 'robotkit', project);

      expect(
        project.readAsStringSync(),
        '<Project Sdk="Microsoft.NET.Sdk">\n'
        '  <ItemGroup>\n'
        '    <PackageReference Include="$robotKit" Version="0.2.0" />\n'
        '    <PackageReference Update="$robotKit" PrivateAssets="all" />\n'
        '  </ItemGroup>\n'
        '  <ItemGroup>\n'
        '  </ItemGroup>\n'
        '</Project>\n',
      );
    },
  );

  test('mod add and remove find items however they are written', () async {
    // MSBuild reads item types and NuGet ids in any case, Include anywhere
    // in the start tag and a `>` inside a value, but no commented-out item.
    // The finder used to read only `<PackageReference Include="id"`, so add
    // appended copies of these items and remove left them in place.
    const commented =
        '    <!-- <PackageReference Include="TopiaForge.Mods.Multiplayer" Version="0.0.1" /> -->';
    const multiplayer =
        '''<PackageReference Condition="'a' != 'b'" Include = "topiaforge.mods.multiplayer"''';
    const generators =
        '<packagereference Include="TopiaForge.Mods.Multiplayer.Generators" Label="a > b"';
    const end = '\n    </packagereference >';
    final project = await _scaffoldModProject(
      currentHarness(),
      't.any-form',
      '<Project Sdk="Microsoft.NET.Sdk">\n'
          '  <ItemGroup>\n'
          '$commented\n'
          '    $multiplayer />\n'
          '    $generators>$end\n'
          '  </ItemGroup>\n'
          '</Project>\n',
    );

    await _runModule(currentHarness(), 'add', 'multiplayer', project);

    const pinnedMultiplayer = '$multiplayer Version="0.2.0" />';
    const pinnedGenerators =
        '$generators Version="0.2.0" PrivateAssets="all">$end';
    const pinned =
        '<Project Sdk="Microsoft.NET.Sdk">\n'
        '  <ItemGroup>\n'
        '$commented\n'
        '    $pinnedMultiplayer\n'
        '    $pinnedGenerators\n'
        '  </ItemGroup>\n'
        '</Project>\n';
    expect(project.readAsStringSync(), pinned);

    await _runModule(currentHarness(), 'remove', 'multiplayer', project);

    expect(
      project.readAsStringSync(),
      pinned
          .replaceFirst(pinnedMultiplayer, '')
          .replaceFirst(pinnedGenerators, ''),
    );
  });

  test('mod add and remove refuse an item shared with other packages', () async {
    // MSBuild includes each id of a list, and NuGet keeps the first item for
    // a package, so a pinned copy left the listed version in effect. An edit
    // to the list itself would change Newtonsoft.Json too.
    const original =
        '<Project Sdk="Microsoft.NET.Sdk">\n'
        '  <ItemGroup>\n'
        '    <PackageReference Include="TopiaForge.Mods.RobotKit;Newtonsoft.Json" Version="0.0.1" />\n'
        '  </ItemGroup>\n'
        '</Project>\n';
    final harness = currentHarness();
    final project = await _scaffoldModProject(harness, 't.shared', original);
    final manifest = File(p.join(project.parent.path, 'topiaforge.mod.json'));
    final scaffolded = manifest.readAsStringSync();

    for (final verb in const ['add', 'remove']) {
      final result = await harness.runCli([
        'mod',
        verb,
        'robotkit',
        '--project',
        project.parent.path,
      ]);

      expect(result.exitCode, 1, reason: '${result.stdout}\n${result.stderr}');
      expect(
        '${result.stderr}',
        allOf(
          contains('TopiaForge.Mods.RobotKit cannot be'),
          contains('Include="TopiaForge.Mods.RobotKit;Newtonsoft.Json"'),
        ),
      );
      expect(project.readAsStringSync(), original);
      expect(manifest.readAsStringSync(), scaffolded);
    }
  });

  test(
    'mod add and remove finish a malformed project within the budget',
    () async {
      // Every start tag runs on to the one `>`, so the old pattern scanned the
      // rest of the file from each of them: about 30 seconds per command.
      final unterminated =
          '${'<PackageReference Include="TopiaForge.Mods.RobotKit"' * 8000}>';
      final project = await _scaffoldModProject(
        currentHarness(),
        't.malformed',
        '<Project Sdk="Microsoft.NET.Sdk">\n$unterminated\n</Project>\n',
      );

      // No item is closed, so add appends one and remove finds only that one.
      const items = {
        'add':
            '<PackageReference Include="TopiaForge.Mods.RobotKit" Version="0.2.0" />',
        'remove': '',
      };
      for (final MapEntry(key: verb, value: item) in items.entries) {
        final watch = Stopwatch()..start();
        await _runModule(currentHarness(), verb, 'robotkit', project);
        watch.stop();
        expect(watch.elapsed, lessThan(const Duration(seconds: 10)));
        expect(
          project.readAsStringSync(),
          '<Project Sdk="Microsoft.NET.Sdk">\n$unterminated\n'
          '  <ItemGroup>\n    $item\n  </ItemGroup>\n</Project>\n',
        );
      }
    },
    timeout: const Timeout(Duration(minutes: 2)),
  );
}

/// Scaffolds mod [id] and replaces its project file with [project].
Future<File> _scaffoldModProject(
  _CliTestHarness harness,
  String id,
  String project,
) async {
  final created = await harness.runCli([
    'new',
    'mod',
    id,
    '--dir',
    harness.temp.path,
  ]);
  expect(created.exitCode, 0, reason: '${created.stdout}\n${created.stderr}');
  return Directory(p.join(harness.temp.path, id))
      .listSync()
      .whereType<File>()
      .singleWhere((file) => p.extension(file.path) == '.csproj')
    ..writeAsStringSync(project);
}

/// Runs `mod <verb> <module>` on the mod that owns [project].
Future<void> _runModule(
  _CliTestHarness harness,
  String verb,
  String module,
  File project,
) async {
  final result = await harness.runCli([
    'mod',
    verb,
    module,
    '--project',
    project.parent.path,
  ]);
  expect(result.exitCode, 0, reason: '${result.stdout}\n${result.stderr}');
}
