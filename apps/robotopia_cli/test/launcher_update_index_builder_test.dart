import 'dart:convert';
import 'dart:io';

import 'package:crypto/crypto.dart';
import 'package:launcher_domain/launcher_domain.dart';
import 'package:path/path.dart' as p;
import 'package:robotopia/src/launcher_update_index_builder.dart';
import 'package:test/test.dart';

void main() {
  late Directory output;

  setUp(() async {
    output = await Directory.systemTemp.createTemp('robotopia-updates-test-');
  });

  tearDown(() async {
    if (await output.exists()) await output.delete(recursive: true);
  });

  test('requires a published stable release', () async {
    final builder = LauncherUpdateIndexBuilder(
      client: _FakeGitHubReleaseClient(releases: const [], assets: const {}),
      clock: _fixedClock,
    );

    await expectLater(
      () => builder.build(_config(output)),
      throwsA(isA<StateError>()),
    );
    expect(output.listSync(), isEmpty);
  });

  test('emits the fail-closed manual release catalog v1', () async {
    final bytes = {
      'win': utf8.encode('windows archive'),
      'mac': utf8.encode('macos archive'),
      'linux': utf8.encode('linux archive'),
    };
    final builder = LauncherUpdateIndexBuilder(
      client: _FakeGitHubReleaseClient(
        releases: [
          _release(
            tagName: 'v1.2.3',
            assets: [
              _asset('QuantumWorks-windows-x64.zip', 'win'),
              _asset('QuantumWorks-macos-universal.zip', 'mac'),
              _asset('QuantumWorks-linux-x64.zip', 'linux'),
            ],
          ),
        ],
        assets: bytes,
      ),
      clock: _fixedClock,
    );

    final result = await builder.build(_config(output));
    final catalog = await _readJson(output, 'manual-releases.json');
    final parsed = ManualReleaseCatalog.fromJson(catalog);

    expect(result.itemCount, 3);
    expect(
      result.manualReleasesUrl,
      'https://owner.github.io/repo/manual-releases.json',
    );
    expect(parsed.isValid, isTrue);
    expect(parsed.formatVersion, 1);
    expect(parsed.manualOnly, isTrue);
    expect(parsed.releaseUrl, endsWith('/releases/tag/v1.2.3'));
    for (final entry in bytes.entries) {
      final platform = entry.key == 'win'
          ? 'windows'
          : entry.key == 'mac'
          ? 'macos'
          : 'linux';
      expect(
        parsed.platforms[platform]!.sha256,
        sha256.convert(entry.value).toString(),
      );
      expect(parsed.platforms[platform]!.size, entry.value.length);
    }
    expect(File(p.join(output.path, 'app-archive.json')).existsSync(), isFalse);
    expect(File(p.join(output.path, 'index.json')).existsSync(), isFalse);
    expect(Directory(p.join(output.path, 'releases')).existsSync(), isFalse);
    expect(jsonEncode(catalog), isNot(contains('minimumUpdaterVersion')));
  });

  test('selects the greatest stable version and ignores prereleases', () async {
    final assets = <String, List<int>>{};
    List<GitHubAsset> platformAssets(String prefix) {
      for (final platform in ['windows', 'macos', 'linux']) {
        assets['$prefix-$platform'] = utf8.encode('$prefix-$platform');
      }
      return [
        for (final platform in ['windows', 'macos', 'linux'])
          _asset('QuantumWorks-$platform.zip', '$prefix-$platform'),
      ];
    }

    final builder = LauncherUpdateIndexBuilder(
      client: _FakeGitHubReleaseClient(
        releases: [
          _release(tagName: 'v1.0.0', assets: platformAssets('old')),
          _release(
            tagName: 'v2.0.0-beta.1',
            prerelease: true,
            assets: platformAssets('beta'),
          ),
          _release(tagName: 'v1.5.0', assets: platformAssets('new')),
        ],
        assets: assets,
      ),
      clock: _fixedClock,
    );

    await builder.build(_config(output));
    final catalog = await _readJson(output, 'manual-releases.json');
    expect(catalog['releaseUrl'], endsWith('/releases/tag/v1.5.0'));
  });

  test(
    'does not silently fall back when the latest stable release is incomplete',
    () async {
      final builder = LauncherUpdateIndexBuilder(
        client: _FakeGitHubReleaseClient(
          releases: [
            _release(
              tagName: 'v2.0.0',
              assets: [_asset('QuantumWorks-windows.zip', 'win')],
            ),
          ],
          assets: {'win': utf8.encode('windows')},
        ),
        clock: _fixedClock,
      );

      await expectLater(
        () => builder.build(_config(output)),
        throwsA(
          isA<StateError>().having(
            (error) => error.message,
            'message',
            contains('missing production assets'),
          ),
        ),
      );
    },
  );

  test('rejects duplicate production assets and insecure URLs', () async {
    final duplicate = LauncherUpdateIndexBuilder(
      client: _FakeGitHubReleaseClient(
        releases: [
          _release(
            tagName: 'v1.0.0',
            assets: [
              _asset('QuantumWorks-windows.zip', 'one'),
              _asset('QuantumWorks-win-x64.zip', 'two'),
            ],
          ),
        ],
        assets: const {},
      ),
      clock: _fixedClock,
    );
    await expectLater(
      () => duplicate.build(_config(output)),
      throwsA(isA<StateError>()),
    );
    expect(
      () => LauncherUpdateIndexConfig(
        repository: 'owner/repo',
        outputDirectory: output.path,
        baseUrl: 'http://updates.example.test',
      ),
      returnsNormally,
    );
    await expectLater(
      () => duplicate.build(
        LauncherUpdateIndexConfig(
          repository: 'owner/repo',
          outputDirectory: output.path,
          baseUrl: 'http://updates.example.test',
        ),
      ),
      throwsA(isA<ArgumentError>()),
    );
  });
}

LauncherUpdateIndexConfig _config(Directory output) =>
    LauncherUpdateIndexConfig(
      repository: 'owner/repo',
      outputDirectory: output.path,
      baseUrl: 'https://owner.github.io/repo/',
    );

DateTime _fixedClock() => DateTime.utc(2026, 1, 2, 3, 4, 5);

GitHubRelease _release({
  required String tagName,
  bool prerelease = false,
  List<GitHubAsset> assets = const [],
}) => GitHubRelease(
  tagName: tagName,
  name: tagName,
  body: '',
  draft: false,
  prerelease: prerelease,
  publishedAt: '2026-01-02T03:04:05Z',
  assets: assets,
);

GitHubAsset _asset(String name, String key) => GitHubAsset(
  name: name,
  apiUrl: 'https://api.github.com/assets/$key',
  browserDownloadUrl: 'https://github.com/owner/repo/releases/download/$key',
);

Future<Map<String, dynamic>> _readJson(Directory output, String name) async =>
    jsonDecode(await File(p.join(output.path, name)).readAsString())
        as Map<String, dynamic>;

class _FakeGitHubReleaseClient implements GitHubReleaseClient {
  _FakeGitHubReleaseClient({required this.releases, required this.assets});

  final List<GitHubRelease> releases;
  final Map<String, List<int>> assets;

  @override
  Future<List<GitHubRelease>> listReleases(String repository) async => releases;

  @override
  Future<Stream<List<int>>> openAsset(GitHubAsset asset) async {
    final bytes = assets[asset.apiUrl.split('/').last];
    if (bytes == null) throw StateError('Missing fake asset bytes.');
    return Stream.value(bytes);
  }
}
