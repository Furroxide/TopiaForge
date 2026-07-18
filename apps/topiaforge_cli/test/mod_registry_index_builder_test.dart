import 'dart:convert';
import 'dart:io';

import 'package:archive/archive.dart';
import 'package:crypto/crypto.dart';
import 'package:launcher_domain/launcher_domain.dart';
import 'package:path/path.dart' as p;
import 'package:topiaforge/src/launcher_update_index_builder.dart';
import 'package:topiaforge/src/mod_registry_index_builder.dart';
import 'package:topiaforge/src/registry_entry_builder.dart';
import 'package:test/test.dart';

void main() {
  late Directory temp;

  setUp(() async {
    temp = await Directory.systemTemp.createTemp('topiaforge-registry-test-');
  });

  tearDown(() async {
    if (await temp.exists()) {
      await temp.delete(recursive: true);
    }
  });

  test('release assets become latest-per-id entries with history', () async {
    final newBytes = _packageBytes(id: 'cool.mod', version: '1.1.0');
    final oldBytes = _packageBytes(id: 'cool.mod', version: '1.0.0');
    final builder = ModRegistryIndexBuilder(
      client: _FakeGitHubReleaseClient(
        releases: [
          _release(
            tagName: 'v2.0.0',
            assets: [_asset('cool.mod-1.1.0.topiaforgemod', 'new')],
          ),
          _release(
            tagName: 'v1.9.0',
            assets: [_asset('cool.mod-1.0.0.topiaforgemod', 'old')],
          ),
          _release(
            tagName: 'v2.1.0-draft',
            draft: true,
            assets: [_asset('cool.mod-9.9.9.topiaforgemod', 'draft')],
          ),
          _release(
            tagName: 'v2.1.0-beta.1',
            prerelease: true,
            assets: [_asset('cool.mod-8.8.8.topiaforgemod', 'beta')],
          ),
        ],
        assets: {
          'new': newBytes,
          'old': oldBytes,
          'draft': _packageBytes(id: 'cool.mod', version: '9.9.9'),
          'beta': _packageBytes(id: 'cool.mod', version: '8.8.8'),
        },
      ),
      clock: _fixedClock,
    );

    final result = await builder.build(
      ModRegistryIndexConfig(
        repository: 'owner/repo',
        outputDirectory: temp.path,
      ),
    );
    final index = _readIndex(temp);
    final mods = (index['mods'] as List).cast<Map<String, Object?>>();

    expect(result.firstPartyCount, 1);
    expect(index['formatVersion'], 2);
    expect(index['generatedAt'], '2026-01-02T03:04:05.000Z');
    expect(index['sourceRepository'], 'https://github.com/owner/repo');
    expect(mods, hasLength(1));

    final mod = RegistryMod.fromJson(mods.single);
    expect(mod.manifest.id, 'cool.mod');
    expect(mod.manifest.version, '1.1.0');
    expect(
      mod.downloadUrl,
      'https://github.com/owner/repo/releases/download/new',
    );
    expect(mod.packageSha256, sha256.convert(newBytes).toString());

    final history = (mods.single['history'] as List).cast<Map>();
    expect(history, hasLength(1));
    expect(history.single['version'], '1.0.0');
    expect(
      history.single['packageSha256'],
      sha256.convert(oldBytes).toString(),
    );
    expect(mods.single['origin'], 'first-party');
  });

  test('directory mode emits relative or base-resolved URLs', () async {
    final packages = Directory(p.join(temp.path, 'packages'))..createSync();
    File(
      p.join(packages.path, 'cool.mod-1.0.0.topiaforgemod'),
    ).writeAsBytesSync(_packageBytes(id: 'cool.mod', version: '1.0.0'));
    final output = Directory(p.join(temp.path, 'out'));

    await ModRegistryIndexBuilder(clock: _fixedClock).build(
      ModRegistryIndexConfig(
        packagesDirectory: packages.path,
        outputDirectory: output.path,
      ),
    );
    var mods = (_readIndex(output)['mods'] as List).cast<Map>();
    expect(mods.single['downloadUrl'], 'cool.mod-1.0.0.topiaforgemod');

    await ModRegistryIndexBuilder(clock: _fixedClock).build(
      ModRegistryIndexConfig(
        packagesDirectory: packages.path,
        outputDirectory: output.path,
        baseUrl: 'https://mods.example.com/files',
      ),
    );
    mods = (_readIndex(output)['mods'] as List).cast<Map>();
    expect(
      mods.single['downloadUrl'],
      'https://mods.example.com/files/cool.mod-1.0.0.topiaforgemod',
    );
  });

  test('community entries merge, sort by id, and carry origin', () async {
    final packages = Directory(p.join(temp.path, 'packages'))..createSync();
    File(
      p.join(packages.path, 'zeta.mod-1.0.0.topiaforgemod'),
    ).writeAsBytesSync(_packageBytes(id: 'zeta.mod', version: '1.0.0'));
    final entries = Directory(p.join(temp.path, 'registry'))..createSync();
    _writeCommunityEntry(
      entries,
      id: 'author.jetpack',
      versions: ['1.1.0', '1.0.0'],
    );
    final output = Directory(p.join(temp.path, 'out'));

    final result = await ModRegistryIndexBuilder(clock: _fixedClock).build(
      ModRegistryIndexConfig(
        packagesDirectory: packages.path,
        entriesDirectory: entries.path,
        outputDirectory: output.path,
      ),
    );
    final mods = (_readIndex(output)['mods'] as List).cast<Map>();

    expect(result.communityCount, 1);
    expect(
      mods.map((mod) => (mod['manifest'] as Map)['name']).toList(),
      ['author.jetpack', 'zeta.mod'],
      reason: 'sorted by id',
    );
    expect(mods.first['origin'], 'community');
    final community = RegistryMod.fromJson(mods.first.cast<String, Object?>());
    expect(community.manifest.version, '1.1.0');
    expect((mods.first['history'] as List), hasLength(1));
  });

  test('community id colliding with first-party fails the build', () async {
    final packages = Directory(p.join(temp.path, 'packages'))..createSync();
    File(
      p.join(packages.path, 'cool.mod-1.0.0.topiaforgemod'),
    ).writeAsBytesSync(_packageBytes(id: 'cool.mod', version: '1.0.0'));
    final entries = Directory(p.join(temp.path, 'registry'))..createSync();
    _writeCommunityEntry(entries, id: 'cool.mod', versions: ['2.0.0']);

    expect(
      () => ModRegistryIndexBuilder(clock: _fixedClock).build(
        ModRegistryIndexConfig(
          packagesDirectory: packages.path,
          entriesDirectory: entries.path,
          outputDirectory: p.join(temp.path, 'out'),
        ),
      ),
      throwsA(
        isA<StateError>().having(
          (error) => error.message,
          'message',
          contains('collides'),
        ),
      ),
    );
  });

  test('reserved community prefixes fail the build', () async {
    final packages = Directory(p.join(temp.path, 'packages'))..createSync();
    final entries = Directory(p.join(temp.path, 'registry'))..createSync();
    _writeCommunityEntry(
      entries,
      id: 'io.github.furroxide.topiaforge.fake',
      versions: ['1.0.0'],
    );

    expect(
      () => ModRegistryIndexBuilder(clock: _fixedClock).build(
        ModRegistryIndexConfig(
          packagesDirectory: packages.path,
          entriesDirectory: entries.path,
          outputDirectory: p.join(temp.path, 'out'),
        ),
      ),
      throwsA(
        isA<StateError>().having(
          (error) => error.message,
          'message',
          contains('reserved'),
        ),
      ),
    );
  });

  test('changelogs directory supplies the latest-version changelog', () async {
    final packages = Directory(p.join(temp.path, 'packages'))..createSync();
    File(
      p.join(packages.path, 'cool.mod-1.1.0.topiaforgemod'),
    ).writeAsBytesSync(_packageBytes(id: 'cool.mod', version: '1.1.0'));
    final mods = Directory(p.join(temp.path, 'mods', 'Cool'))
      ..createSync(recursive: true);
    File(p.join(mods.path, 'topiaforge.mod.json')).writeAsStringSync(
      jsonEncode(_manifestJson(id: 'cool.mod', version: '1.1.0')),
    );
    File(
      p.join(mods.path, 'CHANGELOG.md'),
    ).writeAsStringSync('Fixed timer drift.\n');
    final output = Directory(p.join(temp.path, 'out'));

    await ModRegistryIndexBuilder(clock: _fixedClock).build(
      ModRegistryIndexConfig(
        packagesDirectory: packages.path,
        changelogsDirectory: p.join(temp.path, 'mods'),
        outputDirectory: output.path,
      ),
    );

    final entry = (_readIndex(output)['mods'] as List).single as Map;
    expect(entry['changelog'], 'Fixed timer drift.');
  });

  test('entry builder rejects an old existing registry format', () {
    final result = buildRegistryEntry(
      package: readModPackage(_packageBytes(id: 'cool.mod', version: '1.0.0')),
      downloadUrl: 'https://mods.example.com/cool.mod-1.0.0.topiaforgemod',
      existing: RegistryEntryFile(id: 'cool.mod', formatVersion: 1),
    );

    expect(result.entryFile, isNull);
    expect(
      result.issues.map((issue) => issue.message),
      contains('formatVersion must be 2.'),
    );
  });
}

DateTime _fixedClock() => DateTime.utc(2026, 1, 2, 3, 4, 5);

Map<String, Object?> _manifestJson({
  required String id,
  required String version,
}) {
  return {
    'schemaVersion': 4,
    'name': id,
    'displayName': id,
    'version': version,
    'author': {'name': 'Tester'},
    'license': 'MIT',
    'licenseFiles': ['LICENSE'],
    'entryAssembly': 'Mod.dll',
    'entryType': 'Test.Mod',
    'supportedGameVersionRange': '*',
    'supportedLoaderVersionRange': '*',
    'supportedSdkVersionRange': '*',
  };
}

List<int> _packageBytes({required String id, required String version}) {
  final archive = Archive()
    ..addFile(
      ArchiveFile.string(
        'topiaforge.mod.json',
        jsonEncode(_manifestJson(id: id, version: version)),
      ),
    )
    ..addFile(ArchiveFile.string('LICENSE', 'MIT test fixture license'))
    ..addFile(ArchiveFile.string('Mod.dll', 'dll-bytes-$id-$version'));
  return ZipEncoder().encode(archive);
}

void _writeCommunityEntry(
  Directory entries, {
  required String id,
  required List<String> versions,
}) {
  RegistryEntryFile? entry;
  for (final version in versions.reversed) {
    final result = buildRegistryEntry(
      package: readModPackage(_packageBytes(id: id, version: version)),
      downloadUrl: 'https://mods.example.com/$id-$version.topiaforgemod',
      changelog: 'Notes for $version',
      existing: entry,
    );
    entry = result.entryFile ?? entry;
  }
  final json = const JsonEncoder.withIndent('  ').convert(entry!.toJson());
  File(
    p.join(entries.path, '${id.toLowerCase()}.json'),
  ).writeAsStringSync('$json\n');
}

Map<String, dynamic> _readIndex(Directory output) {
  final text = File(p.join(output.path, 'index.json')).readAsStringSync();
  return jsonDecode(text) as Map<String, dynamic>;
}

GitHubRelease _release({
  required String tagName,
  bool draft = false,
  bool prerelease = false,
  List<GitHubAsset> assets = const [],
}) {
  return GitHubRelease(
    tagName: tagName,
    name: tagName,
    body: '',
    draft: draft,
    prerelease: prerelease,
    publishedAt: '2026-01-02T03:04:05Z',
    assets: assets,
  );
}

GitHubAsset _asset(String name, String key) {
  return GitHubAsset(
    name: name,
    apiUrl: 'https://api.github.com/assets/$key',
    browserDownloadUrl: 'https://github.com/owner/repo/releases/download/$key',
  );
}

class _FakeGitHubReleaseClient implements GitHubReleaseClient {
  _FakeGitHubReleaseClient({required this.releases, required this.assets});

  final List<GitHubRelease> releases;
  final Map<String, List<int>> assets;

  @override
  Future<List<GitHubRelease>> listReleases(String repository) async => releases;

  @override
  Future<Stream<List<int>>> openAsset(GitHubAsset asset) async {
    final key = asset.apiUrl.split('/').last;
    final bytes = assets[key];
    if (bytes == null) {
      throw StateError('Missing fake asset bytes for $key.');
    }
    return Stream.value(bytes);
  }
}
