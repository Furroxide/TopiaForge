import 'dart:convert';
import 'dart:io';

import 'package:launcher_data/src/nuget_lock_check.dart';
import 'package:path/path.dart' as p;
import 'package:test/test.dart';

const _abstractions = 'TopiaForge.Mods.Abstractions';
const _analyzers = 'TopiaForge.Mods.Analyzers';
const _multiplayer = 'TopiaForge.Mods.Multiplayer';
const _sdk = {_abstractions, _analyzers, _multiplayer};
const _version = '0.1.0-rc.1';

/// A lock file with [targets], each a map of package ids to what NuGet
/// resolved for them.
Map<String, Object?> _lock(Map<String, Map<String, String?>> targets) => {
  'version': 1,
  'dependencies': {
    for (final MapEntry(key: target, value: packages) in targets.entries)
      target: {
        for (final MapEntry(key: id, value: resolved) in packages.entries)
          id: resolved == null
              ? {'type': 'Project'}
              : {
                  'type': 'Direct',
                  'requested': '[$resolved, )',
                  'resolved': resolved,
                },
      },
  },
};

List<String> _mismatches(
  Object? lock, {
  Set<String> pinned = const {_abstractions, _analyzers},
}) => [
  for (final mismatch in sdkPinMismatches(
    lock,
    pinned: pinned,
    sdkPackages: _sdk,
    version: _version,
  ))
    '${mismatch.packageId}=${mismatch.resolved.join(',')}',
];

void main() {
  const target = '.NETStandard,Version=v2.1';

  group('sdkPinMismatches', () {
    test('passes a lock that resolves every pinned package at the pin', () {
      expect(
        _mismatches(
          _lock({
            target: {
              _abstractions: _version,
              _analyzers: _version,
              'Newtonsoft.Json': '13.0.3',
            },
          }),
        ),
        isEmpty,
      );
    });

    test('reports another version, and no version, of a pinned package', () {
      expect(
        _mismatches(
          _lock({
            target: {_abstractions: '0.0.1'},
          }),
        ),
        ['$_abstractions=0.0.1', '$_analyzers='],
      );
    });

    test('compares ids and versions without regard to case', () {
      expect(
        _mismatches(
          _lock({
            target: {
              'topiaforge.mods.abstractions': '0.1.0-RC.1',
              'TOPIAFORGE.MODS.ANALYZERS': _version,
            },
          }),
        ),
        isEmpty,
      );
    });

    test('reports an SDK package it resolved at another version, even one '
        'the project is not pinned to', () {
      expect(
        _mismatches(
          _lock({
            target: {
              _abstractions: _version,
              _analyzers: _version,
              _multiplayer: '0.0.9',
            },
          }),
        ),
        ['$_multiplayer=0.0.9'],
      );
    });

    test('reports a project reference in a package\'s place', () {
      expect(
        _mismatches(
          _lock({
            target: {_abstractions: null, _analyzers: _version},
          }),
        ),
        ['$_abstractions=a project reference'],
      );
    });

    test('checks every target, and names each other version once', () {
      expect(
        _mismatches(
          _lock({
            target: {_abstractions: '0.0.1', _analyzers: _version},
            '$target/win-x64': {_abstractions: '0.0.1', _analyzers: '0.0.2'},
            '.NETFramework,Version=v4.7.2': {_analyzers: _version},
          }),
        ),
        ['$_abstractions=0.0.1', '$_analyzers=0.0.2'],
      );
    });

    test('a lock with no target resolved nothing that is pinned', () {
      expect(_mismatches(_lock({})), ['$_abstractions=', '$_analyzers=']);
    });

    test('a runtime\'s target, which lists only what differs for it, need '
        'not list a pinned package, but what it lists is checked', () {
      final pinnedHere = {_abstractions: _version, _analyzers: _version};
      // As NuGet writes it for a project with a RuntimeIdentifier.
      expect(
        _mismatches(_lock({target: pinnedHere, '$target/win-x64': {}})),
        isEmpty,
      );
      expect(
        _mismatches(
          _lock({
            target: pinnedHere,
            '$target/win-x64': {_abstractions: '0.0.1'},
          }),
        ),
        ['$_abstractions=0.0.1'],
      );
      // Without a target framework's own target, nothing pinned resolved.
      expect(_mismatches(_lock({'$target/win-x64': {}})), [
        '$_abstractions=',
        '$_analyzers=',
      ]);
    });

    test('refuses what is not a NuGet lock file', () {
      for (final lock in <Object?>[
        null,
        'text',
        <String, Object?>{},
        {'version': 1, 'dependencies': 'none'},
        {
          'version': 1,
          'dependencies': {target: 'none'},
        },
        {
          'version': 1,
          'dependencies': {
            target: {_abstractions: 'none'},
          },
        },
        {
          'version': 1,
          'dependencies': {
            target: {
              _abstractions: {'type': 'Direct'},
            },
          },
        },
      ]) {
        expect(
          () => _mismatches(lock),
          throwsFormatException,
          reason: jsonEncode(lock),
        );
      }
    });
  });

  group('nuGetLockFileOf', () {
    late Directory root;
    late File project;

    setUp(() {
      root = Directory.systemTemp.createTempSync('tf-nuget-lock-');
      project = File(p.join(root.path, 'Sam Mod.csproj'))
        ..writeAsStringSync('<Project />');
    });
    tearDown(() => root.deleteSync(recursive: true));

    File assets(Object? json) =>
        File(p.join(root.path, 'obj', 'project.assets.json'))
          ..createSync(recursive: true)
          ..writeAsStringSync(jsonEncode(json));

    /// A project file that declares [path] in a plain `<PropertyGroup>`.
    String declaring(String path) =>
        '<Project Sdk="Microsoft.NET.Sdk">\n'
        '  <PropertyGroup>\n'
        '    <TargetFramework>netstandard2.1</TargetFramework>\n'
        '    <NuGetLockFilePath>$path</NuGetLockFilePath>\n'
        '  </PropertyGroup>\n'
        '</Project>\n';

    test('is packages.lock.json beside the project by default', () {
      final location = nuGetLockFileOf(project);
      expect(location.file.path, p.join(root.path, 'packages.lock.json'));
      expect(location.source, NuGetLockFileSource.beside);
    });

    test('is the project-named lock file when there is one', () {
      File(
        p.join(root.path, 'packages.Sam_Mod.lock.json'),
      ).writeAsStringSync('{}');
      final location = nuGetLockFileOf(project);
      expect(
        location.file.path,
        p.join(root.path, 'packages.Sam_Mod.lock.json'),
      );
      expect(location.source, NuGetLockFileSource.projectNamed);
      expect(
        projectNamedNuGetLockFileName(project),
        'packages.Sam_Mod.lock.json',
      );
    });

    test('is where NuGetLockFilePath put it, as the restore recorded', () {
      File(
        p.join(root.path, 'packages.Sam_Mod.lock.json'),
      ).writeAsStringSync('{}');
      assets({
        'project': {
          'restore': {
            'restoreLockProperties': {
              'restorePackagesWithLockFile': 'true',
              'nuGetLockFilePath': 'locks/../locks/sam.lock.json',
            },
          },
        },
      });
      final location = nuGetLockFileOf(
        project,
        projectText: declaring('declared.lock.json'),
      );
      expect(location.file.path, p.join(root.path, 'locks', 'sam.lock.json'));
      expect(location.source, NuGetLockFileSource.recorded);
    });

    test('is where the project declares NuGetLockFilePath when the restore '
        'recorded none, before a project-named lock file', () {
      File(
        p.join(root.path, 'packages.Sam_Mod.lock.json'),
      ).writeAsStringSync('{}');
      final passed = nuGetLockFileOf(
        project,
        projectText: declaring(r'locks\sam.lock.json'),
      );
      expect(passed.file.path, p.join(root.path, 'locks', 'sam.lock.json'));
      expect(passed.source, NuGetLockFileSource.declared);

      // Without the text, the project file itself is read.
      project.writeAsStringSync(declaring('locks/other.lock.json'));
      final read = nuGetLockFileOf(project);
      expect(read.file.path, p.join(root.path, 'locks', 'other.lock.json'));
      expect(read.source, NuGetLockFileSource.declared);
    });

    test('falls back when the restore\'s record names no path or cannot be '
        'read', () {
      for (final json in <Object?>[
        {
          'project': {
            'restore': {
              'restoreLockProperties': {'restorePackagesWithLockFile': 'true'},
            },
          },
        },
        {
          'project': {
            'restore': {
              'restoreLockProperties': {'nuGetLockFilePath': '  '},
            },
          },
        },
        'not an assets file',
        [1, 2],
      ]) {
        assets(json);
        expect(
          nuGetLockFileOf(project).file.path,
          p.join(root.path, 'packages.lock.json'),
          reason: jsonEncode(json),
        );
      }
      File(
        p.join(root.path, 'obj', 'project.assets.json'),
      ).writeAsStringSync('{not json');
      expect(
        nuGetLockFileOf(project).file.path,
        p.join(root.path, 'packages.lock.json'),
      );
    });
  });

  test('a snapshot tells a lock file its restore wrote from one it left as '
      'it was', () {
    final root = Directory.systemTemp.createTempSync('tf-nuget-lock-time-');
    addTearDown(() => root.deleteSync(recursive: true));
    final project = File(p.join(root.path, 'Sam Mod.csproj'))
      ..writeAsStringSync(
        '<Project><PropertyGroup><NuGetLockFilePath>locks/declared.json'
        '</NuGetLockFilePath></PropertyGroup></Project>',
      );
    final hourAgo = DateTime.now().subtract(const Duration(hours: 1));
    File place(String name, DateTime time) => File(p.join(root.path, name))
      ..createSync(recursive: true)
      ..writeAsStringSync('{}')
      ..setLastModifiedSync(time);
    final beside = place('packages.lock.json', hourAgo);
    final named = place('packages.Sam_Mod.lock.json', hourAgo);
    final declared = place(p.join('locks', 'declared.json'), hourAgo);

    final snapshot = NuGetLockFileSnapshot.of(project);

    expect(
      snapshot.paths.toSet(),
      {beside.path, named.path, declared.path}.map(p.normalize).toSet(),
    );
    // Left as they were an hour ago: none is the lock file the restore used.
    for (final lock in [beside, named, declared]) {
      expect(snapshot.wrote(lock), isFalse, reason: lock.path);
    }
    // Rewritten, even with an earlier time, as a share whose clock runs
    // behind would give it.
    named.setLastModifiedSync(hourAgo.add(const Duration(seconds: 1)));
    declared.setLastModifiedSync(hourAgo.subtract(const Duration(hours: 1)));
    expect(snapshot.wrote(named), isTrue);
    expect(snapshot.wrote(declared), isTrue);
    // Not there before.
    expect(snapshot.wrote(place('created.json', hourAgo)), isTrue);
    // Gone since.
    beside.deleteSync();
    expect(snapshot.wrote(beside), isFalse);

    // Rewritten in the time step it had, as a quick restore can be on a file
    // system that keeps whole or two-second steps: its time is no earlier
    // than the snapshot.
    final other = File(p.join(root.path, 'other', 'Other.csproj'))
      ..createSync(recursive: true)
      ..writeAsStringSync('<Project />');
    final otherLock = place(
      p.join('other', 'packages.lock.json'),
      DateTime.now(),
    );
    expect(NuGetLockFileSnapshot.of(other).wrote(otherLock), isTrue);
  });

  group('declaredNuGetLockFilePath', () {
    test('reads the last literal path a plain PropertyGroup declares', () {
      expect(
        declaredNuGetLockFilePath('''
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>netstandard2.1</TargetFramework>
    <NuGetLockFilePath>first.lock.json</NuGetLockFilePath>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="TopiaForge.Mods.Abstractions" Version="1.0.0" />
  </ItemGroup>
  <PropertyGroup>
    <!-- <NuGetLockFilePath>commented.lock.json</NuGetLockFilePath> -->
    <NuGetLockFilePath>
      locks/project.lock.json
    </NuGetLockFilePath>
  </PropertyGroup>
  <Target Name="After" AfterTargets="Build">
    <PropertyGroup>
      <NuGetLockFilePath>target.lock.json</NuGetLockFilePath>
    </PropertyGroup>
  </Target>
</Project>
'''),
        'locks/project.lock.json',
      );
    });

    test('reads none that is conditioned, inside a target or a choose, '
        'commented out, empty or an expression', () {
      const declaration =
          '<PropertyGroup><NuGetLockFilePath>a.lock.json</NuGetLockFilePath>'
          '</PropertyGroup>';
      for (final project in [
        '<Project />',
        '<Project><PropertyGroup Condition="\'\$(CI)\' == \'true\'">'
            '<NuGetLockFilePath>a.lock.json</NuGetLockFilePath>'
            '</PropertyGroup></Project>',
        '<Project><PropertyGroup><NuGetLockFilePath Condition="true">'
            'a.lock.json</NuGetLockFilePath></PropertyGroup></Project>',
        '<Project><Target Name="T">$declaration</Target></Project>',
        '<Project><Choose><When Condition="true">$declaration</When>'
            '</Choose></Project>',
        '<Project><Choose><When Condition="true"><Choose><Otherwise>'
            '<PropertyGroup /></Otherwise></Choose></When>'
            '<Otherwise>$declaration</Otherwise></Choose></Project>',
        '<Project><Target Name="T">$declaration</Project>',
        '<Project><PropertyGroup><!-- <NuGetLockFilePath>a.lock.json'
            '</NuGetLockFilePath> --></PropertyGroup></Project>',
        '<Project><PropertyGroup><NuGetLockFilePath></NuGetLockFilePath>'
            '</PropertyGroup></Project>',
        '<Project><PropertyGroup><NuGetLockFilePath />'
            '</PropertyGroup></Project>',
        '<Project><PropertyGroup><NuGetLockFilePath>'
            '\$(MSBuildProjectDirectory)/a.lock.json</NuGetLockFilePath>'
            '</PropertyGroup></Project>',
        '<Project>$declaration<PropertyGroup><NuGetLockFilePath>\$(Later)'
            '</NuGetLockFilePath></PropertyGroup></Project>',
      ]) {
        expect(declaredNuGetLockFilePath(project), isNull, reason: project);
      }
    });

    test('reads past end tags inside comments, which close nothing', () {
      expect(
        declaredNuGetLockFilePath('''
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <!-- </PropertyGroup> -->
    <NuGetLockFilePath>after-comment.lock.json</NuGetLockFilePath>
  </PropertyGroup>
  <Target Name="Late">
    <!-- </Target> <![CDATA[ </Target> ]]> -->
    <PropertyGroup>
      <NuGetLockFilePath>in-target.lock.json</NuGetLockFilePath>
    </PropertyGroup>
  </Target>
</Project>
'''),
        'after-comment.lock.json',
      );
    });

    test('reads a plain PropertyGroup after a closed target or choose', () {
      const declaration =
          '<PropertyGroup><NuGetLockFilePath>a.lock.json</NuGetLockFilePath>'
          '</PropertyGroup>';
      for (final project in [
        '<Project><Target Name="T"><Message Text="x" /></Target>$declaration'
            '</Project>',
        '<Project><Choose><When Condition="true"><Choose><Otherwise>'
            '<PropertyGroup /></Otherwise></Choose></When></Choose>'
            '$declaration</Project>',
        '<Project><Target Name="T" />$declaration</Project>',
      ]) {
        expect(
          declaredNuGetLockFilePath(project),
          'a.lock.json',
          reason: project,
        );
      }
    });
  });
}
