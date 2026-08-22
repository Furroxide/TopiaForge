import 'package:launcher_domain/launcher_domain.dart';
import 'package:test/test.dart';

void main() {
  test('accepts the current Windows and Linux update matrix', () {
    final candidate = _candidate({
      'windows-x64': _artifact('windows-x64'),
      'linux-x64': _artifact('linux-x64'),
    });

    expect(candidate.platforms.keys.toSet(), {'windows-x64', 'linux-x64'});
  });

  test('keeps optional future macOS support', () {
    final candidate = _candidate({
      'windows-x64': _artifact('windows-x64'),
      'linux-x64': _artifact('linux-x64'),
      'macos-universal': _artifact('macos-universal'),
    });

    expect(candidate.platforms, contains('macos-universal'));
  });

  test('rejects missing or unsupported platform entries', () {
    expect(
      () => _candidate({'windows-x64': _artifact('windows-x64')}),
      throwsFormatException,
    );
    expect(
      () => _candidate({
        'windows-x64': _artifact('windows-x64'),
        'linux-x64': _artifact('linux-x64'),
        'freebsd-x64': _artifact('linux-x64'),
      }),
      throwsFormatException,
    );
  });
}

LauncherUpdateCandidate _candidate(
  Map<String, Map<String, Object?>> platforms,
) => LauncherUpdateCandidate.fromVerifiedJson(
  signingKeyId: 'ed25519:0123456789abcdef',
  payloadSha256:
      'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa',
  json: {
    r'$schema':
        'https://raw.githubusercontent.com/furroxide/TopiaForge/main/'
        'schemas/topiaforge.launcher-update-v1.schema.json',
    'formatVersion': 1,
    'product': 'TopiaForge',
    'version': '0.1.0-rc.1',
    'tag': 'v0.1.0-rc.1',
    'channel': 'beta',
    'minimumUpdaterVersion': '0.1.0-rc.1',
    'releaseUrl':
        'https://github.com/furroxide/TopiaForge/releases/tag/'
        'v0.1.0-rc.1',
    'platforms': platforms,
  },
);

Map<String, Object?> _artifact(String platform) {
  const names = {
    'windows-x64': 'TopiaForge-windows-x64.zip',
    'linux-x64': 'TopiaForge-linux-x64.zip',
    'macos-universal': 'TopiaForge-macos-universal.zip',
  };
  final name = names[platform]!;
  return {
    'assetName': name,
    'url':
        'https://github.com/furroxide/TopiaForge/releases/download/'
        'v0.1.0-rc.1/$name',
    'sha256':
        'bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb',
    'size': 1024,
    'entryCount': 2,
    'expandedSize': 2048,
    'installLayout': platform == 'macos-universal'
        ? 'app-bundle'
        : 'portable-root',
  };
}
