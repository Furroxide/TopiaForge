import 'dart:convert';
import 'dart:io';

import 'package:crypto/crypto.dart';
import 'package:path/path.dart' as p;
import 'package:robotopia/src/launcher_update_index_builder.dart';
import 'package:test/test.dart';

void main() {
  late Directory output;

  setUp(() async {
    output = await Directory.systemTemp.createTemp('robotopia-updates-test-');
  });

  tearDown(() async {
    if (await output.exists()) {
      await output.delete(recursive: true);
    }
  });

  test('empty release list produces a valid empty archive', () async {
    final builder = LauncherUpdateIndexBuilder(
      client: _FakeGitHubReleaseClient(releases: const [], assets: const {}),
      clock: _fixedClock,
    );

    final result = await builder.build(_config(output));
    final archive = await _readJson(output, 'app-archive.json');

    expect(result.itemCount, 0);
    expect(await File(p.join(output.path, '.nojekyll')).exists(), isTrue);
    expect(await File(p.join(output.path, 'index.json')).exists(), isTrue);
    expect(archive['schemaVersion'], 3);
    expect(archive['items'], isEmpty);
    expect((archive['channels'] as Map).keys, ['release', 'beta', 'nightly']);
  });

  test('release with three platform assets writes three descriptors', () async {
    final assets = {
      'win': utf8.encode('windows archive'),
      'mac': utf8.encode('macos archive'),
      'linux': utf8.encode('linux archive'),
      'debug': utf8.encode('debug archive'),
    };
    final builder = LauncherUpdateIndexBuilder(
      client: _FakeGitHubReleaseClient(
        releases: [
          _release(
            tagName: 'v1.2.3+45',
            body: 'mandatory-update: true',
            assets: [
              _asset('QuantumWorks-windows-x64.zip', 'win'),
              _asset('QuantumWorks-macos-universal.zip', 'mac'),
              _asset('QuantumWorks-linux-x64.zip', 'linux'),
              _asset('QuantumWorks-linux-debug.zip', 'debug'),
            ],
          ),
        ],
        assets: assets,
      ),
      clock: _fixedClock,
    );

    final result = await builder.build(_config(output));
    final archive = await _readJson(output, 'app-archive.json');

    expect(result.itemCount, 3);
    expect(archive['items'], hasLength(3));
    for (final platform in ['windows', 'macos', 'linux']) {
      final descriptor = await _readJson(
        output,
        p.join('releases', '1.2.3+45', 'release', platform, 'release.json'),
      );
      expect(descriptor['version'], '1.2.3');
      expect(descriptor['buildNumber'], 45);
      expect(
        descriptor['appName'],
        platform == 'macos' ? 'QuantumWorks.app' : 'QuantumWorks',
      );
      expect(descriptor['platform'], platform);
      expect(descriptor['channel'], 'release');
      expect(descriptor['minimumUpdaterVersion'], '2.4.2');
    }
  });

  test('same-version builds write unique matching descriptors', () async {
    final builder = LauncherUpdateIndexBuilder(
      client: _FakeGitHubReleaseClient(
        releases: [
          _release(
            tagName: 'v4.0.0+1',
            assets: [_asset('QuantumWorks-windows.zip', 'win1')],
          ),
          _release(
            tagName: 'v4.0.0+2',
            assets: [_asset('QuantumWorks-windows.zip', 'win2')],
          ),
        ],
        assets: {
          'win1': utf8.encode('windows build 1'),
          'win2': utf8.encode('windows build 2'),
        },
      ),
      clock: _fixedClock,
    );

    await builder.build(_config(output));
    final archive = await _readJson(output, 'app-archive.json');
    final first = await _readJson(
      output,
      p.join('releases', '4.0.0+1', 'release', 'windows', 'release.json'),
    );
    final second = await _readJson(
      output,
      p.join('releases', '4.0.0+2', 'release', 'windows', 'release.json'),
    );
    final items = (archive['items'] as List).cast<Map>();
    final channels = archive['channels'] as Map;
    final release = channels['release'] as Map;
    final latest = release['latest'] as Map;

    expect(first['buildNumber'], 1);
    expect(second['buildNumber'], 2);
    expect(items.map((item) => item['buildNumber']), containsAll([1, 2]));
    expect(
      items.map((item) => item['release']),
      containsAll([
        'https://owner.github.io/repo/releases/4.0.0+1/release/windows/release.json',
        'https://owner.github.io/repo/releases/4.0.0+2/release/windows/release.json',
      ]),
    );
    expect(latest['version'], '4.0.0');
    expect(latest['buildNumber'], 2);
  });

  test('infers channels and honors explicit update-channel override', () async {
    final builder = LauncherUpdateIndexBuilder(
      client: _FakeGitHubReleaseClient(
        releases: [
          _release(
            tagName: 'v2.0.0-beta.1',
            prerelease: true,
            assets: [_asset('QuantumWorks-linux.zip', 'beta')],
          ),
          _release(
            tagName: 'v2.1.0-canary.1',
            prerelease: true,
            assets: [_asset('QuantumWorks-linux.zip', 'nightly')],
          ),
          _release(
            tagName: 'v2.2.0-beta.1',
            body: 'update-channel: release',
            prerelease: true,
            assets: [_asset('QuantumWorks-linux.zip', 'release')],
          ),
        ],
        assets: {
          'beta': utf8.encode('beta'),
          'nightly': utf8.encode('nightly'),
          'release': utf8.encode('release'),
        },
      ),
      clock: _fixedClock,
    );

    await builder.build(_config(output));
    final archive = await _readJson(output, 'app-archive.json');
    final channels = [
      for (final item in archive['items'] as List)
        (item as Map)['channel'] as String,
    ];

    expect(channels, containsAll(['beta', 'nightly', 'release']));
  });

  test('emits mandatory flag, artifact hash, and artifact length', () async {
    final bytes = utf8.encode('zip payload');
    final builder = LauncherUpdateIndexBuilder(
      client: _FakeGitHubReleaseClient(
        releases: [
          _release(
            tagName: 'v3.0.0',
            body: 'mandatory-update: true',
            assets: [_asset('QuantumWorks-windows.zip', 'win')],
          ),
        ],
        assets: {'win': bytes},
      ),
      clock: _fixedClock,
    );

    await builder.build(_config(output));
    final archive = await _readJson(output, 'app-archive.json');
    final descriptor = await _readJson(
      output,
      p.join('releases', '3.0.0', 'release', 'windows', 'release.json'),
    );
    final item = (archive['items'] as List).single as Map;
    final artifact = descriptor['artifact'] as Map;

    expect(item['mandatory'], isTrue);
    expect(artifact['length'], bytes.length);
    expect(artifact['sha256'], sha256.convert(bytes).toString());
  });
}

LauncherUpdateIndexConfig _config(Directory output) {
  return LauncherUpdateIndexConfig(
    repository: 'owner/repo',
    outputDirectory: output.path,
    baseUrl: 'https://owner.github.io/repo/',
  );
}

DateTime _fixedClock() => DateTime.utc(2026, 1, 2, 3, 4, 5);

GitHubRelease _release({
  required String tagName,
  String body = '',
  bool prerelease = false,
  List<GitHubAsset> assets = const [],
}) {
  return GitHubRelease(
    tagName: tagName,
    name: tagName,
    body: body,
    draft: false,
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

Future<Map<String, dynamic>> _readJson(
  Directory output,
  String relativePath,
) async {
  final text = await File(p.join(output.path, relativePath)).readAsString();
  return jsonDecode(text) as Map<String, dynamic>;
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
