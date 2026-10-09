import 'dart:convert';
import 'dart:io';

import 'package:archive/archive.dart';
import 'package:path/path.dart' as p;
import 'package:test/test.dart';
import 'package:topiaforge/src/release_dart_runtime_packages.dart';
import 'package:topiaforge/src/release_package_models.dart';
import 'package:topiaforge/src/release_policy.dart';
import 'package:topiaforge/src/release_spdx_third_party.dart';

/// The third-party inventory is what lets the release SBOM describe vendored
/// code truthfully. These tests tie every record to the repository file it
/// comes from, so a licence cannot drift from what the repository records and a
/// shipped package cannot drop out of the SBOM unnoticed.
void main() {
  final root = _repositoryRoot();
  final policy = TopiaForgeReleasePolicy.load(root);
  final release = TopiaForgeReleaseCatalog.load(root).release('0.1.0-rc.1');
  final inventory = ReleaseSpdxThirdPartyInventory.load(
    repositoryRoot: root,
    policy: policy,
    release: release,
  );
  final notices = File(
    p.join(root, 'THIRD_PARTY_NOTICES.md'),
  ).readAsStringSync().replaceAll('\r\n', '\n');
  final prose = notices.replaceAll(RegExp(r'\s+'), ' ');

  test('vendored components map to the catalog component that ships them', () {
    final vendored = {
      for (final entry in inventory.packages)
        if (entry.ecosystem != 'pub')
          entry.name:
              '${entry.version} ${entry.licenseDeclared} '
              '${(entry.shippedBy.toList()..sort()).join(',')}',
    };
    expect(vendored, {
      '.NET Runtime': '10.0.9 MIT gameCompatExtractor',
      'Audiowide': 'null OFL-1.1 launcherUi,unityUi',
      'City Mega Pack': 'null CC0-1.0 launcherUi',
      'Dart SDK': '3.12.2 BSD-3-Clause cli,launcher',
      'Flutter': '3.44.6 NOASSERTION launcher',
      'Gum Bot sprites': 'null CC0-1.0 launcherUi',
      'Harmony': 'null MIT bepInEx',
      'HarmonyX': '2.7.0 MIT bepInEx',
      'Liberation Sans': 'null OFL-1.1 unityUi',
      'Material Icons': 'null NOASSERTION launcher',
      'Mono.Cecil': '0.10.4 MIT bepInEx',
      'MonoMod': '21.12.13.01 MIT bepInEx',
      'Quicksand': 'null OFL-1.1 launcherUi,unityUi',
      'SPDX License List Data': '3.28.0 CC0-1.0 cli',
      'System.Collections.Immutable': '10.0.9 MIT loader',
      'System.Reflection.Metadata': '10.0.9 MIT loader',
      'System.Reflection.MetadataLoadContext': '10.0.9 MIT gameCompatExtractor',
      'TextMesh Pro essential resources':
          'null LicenseRef-Unity-Companion-License unityUi',
      'UnityDoorstop': '4.5.0 LGPL-2.1-only bepInEx',
    });
  });

  test('each executable ships exactly its locked runtime packages', () {
    for (final shipment in releaseDartShipments.entries) {
      final shipped = {
        for (final entry in inventory.packages)
          if (entry.ecosystem == 'pub' &&
              entry.shippedBy.contains(shipment.key))
            entry.name: entry.version,
      };
      expect(
        shipped,
        readLockedHostedVersions(
          File(p.join(root, shipment.value.lockfile)),
          shipment.value.packages,
        ),
        reason: shipment.key,
      );
    }
    final collection = inventory.packages.singleWhere(
      (entry) => entry.ecosystem == 'pub' && entry.name == 'collection',
    );
    expect(collection.shippedBy, {'cli', 'launcher'});
    expect(collection.purl, 'pkg:pub/collection@1.19.1');
  });

  test('the CLI runtime list is the resolved runtime package graph', () {
    final graph = File(
      p.join(root, 'apps/topiaforge_cli/.dart_tool/package_graph.json'),
    );
    expect(graph.existsSync(), isTrue, reason: 'run dart pub get first');
    expect(
      _hostedRuntimeClosure(
        graph,
        File(p.join(root, releaseDartShipments['cli']!.lockfile)),
      ),
      dartCliRuntimePackages.toSet(),
    );
  });

  final launcherLockfile = File(
    p.join(root, releaseDartShipments['launcher']!.lockfile),
  );
  test('the launcher runtime list partitions its lockfile', () {
    final locked = readPubLockfile(launcherLockfile);
    final runtime = launcherRuntimePackages.toSet();
    expect(runtime.intersection(_launcherDevOnlyPackages), isEmpty);
    expect(
      {
        for (final entry in locked.entries)
          if (entry.value.source == 'hosted') entry.key,
      },
      runtime.union(_launcherDevOnlyPackages),
      reason:
          'classify every new hosted package as runtime or dev-only with '
          '`flutter pub deps --no-dev` in apps/topiaforge_launcher_flutter',
    );
    for (final name in runtime) {
      expect(locked[name]!.dependency, isNot('direct dev'), reason: name);
    }
  });

  final launcherGraph = File(
    p.join(
      root,
      'apps/topiaforge_launcher_flutter/.dart_tool/package_graph.json',
    ),
  );
  test(
    'the launcher runtime list is the resolved runtime package graph',
    () => expect(
      _hostedRuntimeClosure(launcherGraph, launcherLockfile),
      launcherRuntimePackages.toSet(),
    ),
    skip: launcherGraph.existsSync()
        ? false
        : 'run flutter pub get in apps/topiaforge_launcher_flutter to resolve '
              'its package graph',
  );

  test('recorded Dart licences mirror the THIRD_PARTY_NOTICES table', () {
    final header = notices.indexOf(
      '| Component | Pinned release resolution | License |',
    );
    expect(header, isNonNegative);
    final rows = <String, String>{};
    for (final line in notices.substring(header).split('\n').skip(2)) {
      final row = RegExp(
        r'^\| ([^|]+?) \| ([^|]+?) \| ([^|]+?) \|$',
      ).firstMatch(line);
      if (row == null) break;
      rows['${row[1]}@${row[2]}'] = row[3]!.split(' ').first;
    }
    expect(rows, contains('Dart SDK@3.12.2'));
    expect(
      Map.of(releaseSpdxRecordedLicenses)..removeWhere(
        (key, _) => key.startsWith('.NET ') || key.startsWith('System.'),
      ),
      rows,
    );
  });

  test('the GameCompat extractor licences are recorded as MIT', () {
    for (final phrase in const [
      '`TopiaForge.GameCompat.Extractor` embeds .NET Runtime 10.0.9.',
      'copies the identical .NET Foundation MIT text from the pinned runtime '
          'pack',
      '`System.Reflection.MetadataLoadContext` 10.0.9. That NuGet package '
          'declares MIT',
      'Both signed NuGet packages declare MIT',
    ]) {
      expect(prose, contains(phrase));
    }
  });

  test('vendored records match their provenance files', () {
    final bepInEx = _json(
      File(p.join(root, 'third_party/BepInEx/provenance.json')),
    );
    final doorstop = (bepInEx['correspondingSource'] as List)
        .cast<Map>()
        .single;
    final record = _named(inventory, 'UnityDoorstop');
    expect(doorstop['component'], record.name);
    expect(doorstop['version'], record.version);
    expect(doorstop['sourceUrl'], record.downloadLocation);
    expect(doorstop['licenseFile'], 'LICENSES/UnityDoorstop-LGPL-2.1.txt');
    expect(
      releaseVendoredComponents['bepInEx']!.downloadLocation,
      'git+${bepInEx['sourceRepository']}@v${bepInEx['version']}',
    );
    final table = {
      for (final row in RegExp(
        r'^\| ([^|]+?) \| ([^|]+?) \| ([^|]+?) \| [^|]+ \| [^|]+ \|$',
        multiLine: true,
      ).allMatches(notices))
        row[1]!: '${row[2]!.split(' / ').first} ${row[3]}',
    };
    for (final name in const ['HarmonyX', 'MonoMod', 'Mono.Cecil']) {
      final entry = _named(inventory, name);
      expect(table[name], '${entry.version} ${entry.licenseDeclared}');
    }
    expect(table['Harmony'], 'HarmonyX upstream base MIT');
    expect(table['UnityDoorstop'], startsWith('4.5.0 LGPL-2.1'));
    // The Windows bundle that packaging ships carries each recorded component.
    final tree = _json(
      File(
        p.join(
          root,
          'third_party/BepInEx',
          '${ReleasePackagePlatform.windows.bepInExBundleName}.tree.json',
        ),
      ),
    );
    expect(
      (tree['files'] as Map).keys,
      containsAll(const [
        'winhttp.dll',
        'BepInEx/core/0Harmony20.dll',
        'BepInEx/core/0Harmony.dll',
        'BepInEx/core/MonoMod.RuntimeDetour.dll',
        'BepInEx/core/Mono.Cecil.dll',
      ]),
    );

    final spdx = _json(
      File(p.join(root, 'third_party/SPDX_LICENSE_LIST_PROVENANCE.json')),
    );
    final list = _named(inventory, 'SPDX License List Data');
    expect(spdx['version'], list.version);
    expect(spdx['license'], list.licenseDeclared);
    expect(
      list.downloadLocation,
      'git+${spdx['sourceRepository']}@${spdx['tag']}',
    );

    for (final font in const ['Quicksand', 'Audiowide', 'Liberation Sans']) {
      final entry = _projectEntry(notices, font);
      expect(entry, contains('- License: SIL Open Font License 1.1'));
      final download = _named(inventory, font).downloadLocation;
      if (download != 'NOASSERTION') expect(entry, contains(download));
    }
    expect(
      _projectEntry(notices, 'TextMesh Pro essential resources'),
      contains('Unity Companion License'),
    );
    for (final pack in const ['City Mega Pack', 'Gum Bot sprites']) {
      expect(notices, contains('*$pack*'));
      expect(notices, contains(_named(inventory, pack).downloadLocation));
    }
    expect(prose, contains('CC0 1.0 Universal (public domain dedication)'));

    final flutter = _json(
      File(p.join(root, '.github/actions/setup-flutter/releases.json')),
    );
    expect(flutter['flutterVersion'], _named(inventory, 'Flutter').version);
    expect(flutter['dartVersion'], _named(inventory, 'Dart SDK').version);
    expect(
      File(
        p.join(root, 'apps/topiaforge_launcher_flutter/pubspec.yaml'),
      ).readAsStringSync(),
      contains('uses-material-design: true'),
    );
  });

  test('the UnityDoorstop source licenses version 2.1 only', () {
    final archive = ZipDecoder().decodeBytes(
      File(
        p.join(
          root,
          'third_party/BepInEx',
          'UnityDoorstop-4.5.0-source-'
              '33dab9a6733862eb81869ff08431d9478b28784b.zip',
        ),
      ).readAsBytesSync(),
    );
    String text(ArchiveFile file) =>
        utf8.decode(file.readBytes()!, allowMalformed: true);
    ArchiveFile member(String suffix) =>
        archive.files.singleWhere((file) => file.name.endsWith(suffix));
    expect(
      text(member('/README.md')),
      contains('Doorstop 4 is licensed under LGPLv2.1.'),
    );
    expect(
      text(member('/LICENSE')).replaceAll('\r\n', '\n'),
      File(
        p.join(root, 'third_party/BepInEx/LICENSES/UnityDoorstop-LGPL-2.1.txt'),
      ).readAsStringSync().replaceAll('\r\n', '\n'),
    );
    for (final file in archive.files) {
      if (!file.isFile || file.name.endsWith('/LICENSE')) continue;
      expect(
        text(file),
        isNot(contains('any later version')),
        reason: '${file.name} must not grant a later LGPL version',
      );
    }
  });

  test('only components the repository records no licence for are '
      'NOASSERTION', () {
    expect(
      {
        for (final entry in inventory.packages)
          if (entry.ecosystem != 'pub' &&
              entry.licenseDeclared == 'NOASSERTION')
            entry.name,
      },
      {'Flutter', 'Material Icons'},
    );
  });

  test('the inventory refuses pins its records were not verified against', () {
    final moved = TopiaForgeReleaseCatalogEntry(
      version: release.version,
      tag: release.tag,
      prerelease: release.prerelease,
      status: release.status,
      notesFile: release.notesFile,
      components: {...release.components, 'bepInEx': '5.4.24.0'},
      vpmPackages: release.vpmPackages,
      mods: release.mods,
      excludedDeveloperMods: release.excludedDeveloperMods,
      artifacts: release.artifacts,
    );
    expect(
      () => ReleaseSpdxThirdPartyInventory.load(
        repositoryRoot: root,
        policy: policy,
        release: moved,
      ),
      throwsA(
        isA<StateError>().having(
          (error) => error.message,
          'message',
          contains('Re-verify its third-party records'),
        ),
      ),
    );
  });

  test('the inventory refuses a macOS release until plthook is recorded', () {
    final temp = Directory.systemTemp.createTempSync('topiaforge-sbom-macos-');
    addTearDown(() => temp.deleteSync(recursive: true));
    final policyJson = _json(File(p.join(root, 'release/release-policy.json')));
    (policyJson['artifactPolicy'] as Map)['platformArchives'] = [
      'TopiaForge-macos-universal.zip',
      'TopiaForge-windows-x64.zip',
    ];
    File(p.join(temp.path, 'release/release-policy.json'))
      ..createSync(recursive: true)
      ..writeAsStringSync(jsonEncode(policyJson));
    expect(
      () => ReleaseSpdxThirdPartyInventory.load(
        repositoryRoot: root,
        policy: TopiaForgeReleasePolicy.load(temp.path),
        release: release,
      ),
      throwsA(
        isA<StateError>().having(
          (error) => error.message,
          'message',
          contains('before describing a macOS release'),
        ),
      ),
    );
  });

  test('locked versions fail closed for packages the lockfile does not '
      'host', () {
    final lockfile = File(p.join(root, releaseDartShipments['cli']!.lockfile));
    for (final name in const ['left_pad', 'launcher_data']) {
      expect(
        () => readLockedHostedVersions(lockfile, [name]),
        throwsA(
          isA<StateError>().having(
            (error) => error.message,
            'message',
            contains('does not pin $name as a hosted package'),
          ),
        ),
      );
    }
  });
}

/// Hosted packages the launcher resolves only for development and tests.
const _launcherDevOnlyPackages = {
  'args',
  'boolean_selector',
  'clock',
  'fake_async',
  'flutter_lints',
  'icons_launcher',
  'image',
  'leak_tracker',
  'leak_tracker_flutter_testing',
  'leak_tracker_testing',
  'lints',
  'matcher',
  'petitparser',
  'stack_trace',
  'stream_channel',
  'test_api',
  'universal_io',
  'vm_service',
  'xml',
  'yaml',
};

/// Hosted packages reachable from the root's non-dev dependencies.
Set<String> _hostedRuntimeClosure(File graphFile, File lockfile) {
  final graph = _json(graphFile);
  final nodes = {
    for (final entry in (graph['packages'] as List).cast<Map>())
      entry['name'] as String: entry,
  };
  final rootName = (graph['roots'] as List).single as String;
  final pending = [
    ...(nodes[rootName]!['dependencies'] as List).cast<String>(),
  ];
  final reached = <String>{};
  while (pending.isNotEmpty) {
    final name = pending.removeLast();
    if (!reached.add(name)) continue;
    pending.addAll(
      ((nodes[name]?['dependencies'] as List?) ?? const []).cast<String>(),
    );
  }
  final locked = readPubLockfile(lockfile);
  return {
    for (final name in reached)
      if (locked[name]?.source == 'hosted') name,
  };
}

/// The bulleted entry THIRD_PARTY_NOTICES.md keeps for [project].
String _projectEntry(String notices, String project) {
  final start = notices.indexOf('- Project: $project');
  expect(start, isNonNegative, reason: '$project has no notices entry');
  final end = notices.indexOf('\n\n', start);
  return notices.substring(start, end < 0 ? notices.length : end);
}

ReleaseSpdxThirdPartyPackage _named(
  ReleaseSpdxThirdPartyInventory inventory,
  String name,
) => inventory.packages.singleWhere((entry) => entry.name == name);

Map<String, Object?> _json(File file) =>
    (jsonDecode(file.readAsStringSync()) as Map).cast<String, Object?>();

String _repositoryRoot() {
  var directory = Directory.current.absolute;
  while (!File(p.join(directory.path, 'TopiaForge.slnx')).existsSync()) {
    if (directory.parent.path == directory.path) {
      throw StateError('Repository root not found.');
    }
    directory = directory.parent;
  }
  return directory.path;
}
