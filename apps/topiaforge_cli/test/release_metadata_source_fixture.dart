import 'dart:convert';
import 'dart:io';

import 'package:path/path.dart' as p;

/// Metadata tampering tests need a valid prerelease policy. Build an explicit
/// synthetic prerelease from current components without editing real policy or
/// treating the blocked stable target as permission to publish unsigned bytes.
Directory writeMetadataPrereleaseFixture(String source) {
  final output = Directory.systemTemp.createTempSync(
    'topiaforge-policy-fixture-',
  );
  final listing = Process.runSync('git', [
    'ls-files',
    '-z',
  ], workingDirectory: source);
  if (listing.exitCode != 0) {
    throw StateError('Cannot list metadata fixture inputs.');
  }
  final files = (listing.stdout as String)
      .split('\x00')
      .where(
        (name) =>
            name.endsWith('.csproj') ||
            name.endsWith('/pubspec.yaml') ||
            name.startsWith('release/') ||
            name.startsWith('schemas/') ||
            name.startsWith('.github/') && name.endsWith('.json') ||
            name.startsWith('third_party/') ||
            name.startsWith('mods/') &&
                (name.endsWith('.json') ||
                    name.endsWith('.md') ||
                    name.endsWith('LICENSE')) ||
            name.startsWith('packages/launcher_ui/fonts/') ||
            name.startsWith(
              'tools/unity-ui-bundle/Assets/TextMesh Pro/Fonts/',
            ) ||
            name.startsWith(
                  'templates/TopiaForge.UnityWorldTemplate/Packages/',
                ) &&
                (name.endsWith('package.json') ||
                    name.endsWith('LICENSE.md')) ||
            const {
              'global.json',
              'Directory.Build.props',
              'LICENSE',
              'DCO',
              'THIRD_PARTY_NOTICES.md',
              'packages/launcher_ui/LICENSE',
              'baselines/gamecode.surface.baseline.json',
              'src/TopiaForge.ModManager.Core/TopiaForgeVersions.cs',
              'src/TopiaForge.Mods.UnityUi/Assets/topiaforge-ui.manifest.json',
              'src/TopiaForge.Mods.UnityUi/Assets/topiaforge-ui.bundle',
              'tools/unity-ui-bundle/ProjectSettings/ProjectVersion.txt',
            }.contains(name),
      );
  for (final name in {...files, 'release/notes/v0.2.0.md'}) {
    final destination = File(p.join(output.path, name))
      ..parent.createSync(recursive: true);
    File(p.join(source, name)).copySync(destination.path);
  }
  Map<String, dynamic> read(String name) =>
      jsonDecode(File(p.join(output.path, name)).readAsStringSync())
          as Map<String, dynamic>;
  void write(String name, Object value) =>
      File(p.join(output.path, name)).writeAsStringSync(jsonEncode(value));
  final policy = read('release/release-policy.json');
  (policy['versioning'] as Map)['productVersion'] = '0.2.0-rc.1';
  write('release/release-policy.json', policy);
  final catalog = read('release/catalog.json');
  final current = Map<String, dynamic>.from(
    (catalog['releases'] as List).singleWhere(
          (entry) => (entry as Map)['version'] == '0.2.0',
        )
        as Map,
  );
  current.addAll({
    'version': '0.2.0-rc.1',
    'tag': 'v0.2.0-rc.1',
    'prerelease': true,
    'status': 'ready',
  });
  catalog['releases'] = [current];
  write('release/catalog.json', catalog);
  return output;
}
