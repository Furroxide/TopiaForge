import 'dart:convert';
import 'dart:io';
import 'dart:typed_data';

import 'package:launcher_data/launcher_data.dart';
import 'package:launcher_domain/launcher_domain.dart';
import 'package:path/path.dart' as p;
import 'package:test/test.dart';

void main() {
  late Directory root;
  late LauncherUpdateKeyMaterial key;
  late _FakeUpdateTransport transport;

  setUp(() async {
    root = await Directory.systemTemp.createTemp('topiaforge-update-check-');
    key = await LauncherUpdateKeyMaterial.generate();
    transport = _FakeUpdateTransport();
  });

  tearDown(() async {
    if (root.existsSync()) await root.delete(recursive: true);
  });

  test('discovers a newer beta only after signature verification', () async {
    await _publishFixture(transport, key, version: '1.0.0-rc.2');
    final repository = LocalLauncherUpdateRepository(
      dataRoot: root.path,
      transport: transport,
      trustStore: LauncherUpdateTrustStore([key.publicKey]),
    );
    addTearDown(repository.dispose);

    final status = await repository.checkForUpdate(
      currentVersion: '1.0.0-rc.1',
      channel: LauncherUpdateChannel.beta,
      force: true,
    );

    expect(status.phase, LauncherUpdatePhase.available);
    expect(status.candidate?.version, '1.0.0-rc.2');
    expect(status.candidate?.signingKeyId, key.publicKey.id);
    expect(
      transport.fetched,
      containsAll([
        LocalLauncherUpdateRepository.releasesApi,
        _assetUri('1.0.0-rc.2', 'topiaforge-update-v1.json'),
        _assetUri('1.0.0-rc.2', 'topiaforge-update-v1.json.sig'),
      ]),
    );
  });

  test(
    'stable channel ignores prereleases without fetching their payload',
    () async {
      await _publishFixture(transport, key, version: '1.0.0-rc.2');
      final repository = LocalLauncherUpdateRepository(
        dataRoot: root.path,
        transport: transport,
        trustStore: LauncherUpdateTrustStore([key.publicKey]),
      );
      addTearDown(repository.dispose);

      final status = await repository.checkForUpdate(
        currentVersion: '1.0.0-rc.1',
        channel: LauncherUpdateChannel.release,
        force: true,
      );

      expect(status.phase, LauncherUpdatePhase.current);
      expect(transport.fetched, [LocalLauncherUpdateRepository.releasesApi]);
    },
  );

  test('tampering and GitHub reconciliation mismatches fail closed', () async {
    await _publishFixture(
      transport,
      key,
      version: '1.0.0-rc.2',
      signedReleaseUrl:
          'https://github.com/furroxide/TopiaForge/releases/tag/v1.0.0-rc.3',
    );
    final repository = LocalLauncherUpdateRepository(
      dataRoot: root.path,
      transport: transport,
      trustStore: LauncherUpdateTrustStore([key.publicKey]),
    );
    addTearDown(repository.dispose);

    final mismatch = await repository.checkForUpdate(
      currentVersion: '1.0.0-rc.1',
      channel: LauncherUpdateChannel.beta,
      force: true,
    );
    expect(mismatch.phase, LauncherUpdatePhase.failed);
    expect(mismatch.message, contains('does not match its GitHub release'));

    await _publishFixture(transport, key, version: '1.0.0-rc.2');
    final signatureUri = _assetUri(
      '1.0.0-rc.2',
      'topiaforge-update-v1.json.sig',
    );
    final changedSignature = Uint8List.fromList(
      transport.resources[signatureUri]!,
    );
    final sidecar = Map<String, Object?>.from(
      jsonDecode(utf8.decode(changedSignature)) as Map,
    );
    final rawSignature = base64Decode(sidecar['signature']! as String);
    rawSignature[0] ^= 1;
    sidecar['signature'] = base64Encode(rawSignature);
    transport.resources[signatureUri] = Uint8List.fromList(
      utf8.encode('${const JsonEncoder.withIndent('  ').convert(sidecar)}\n'),
    );
    final tampered = await repository.checkForUpdate(
      currentVersion: '1.0.0-rc.1',
      channel: LauncherUpdateChannel.beta,
      force: true,
    );
    expect(tampered.phase, LauncherUpdatePhase.failed);
    expect(tampered.message, contains('signature'));
  });

  test('persists a highest-seen version to reject signed replay', () async {
    await _publishFixture(transport, key, version: '1.0.0-rc.3');
    final repository = LocalLauncherUpdateRepository(
      dataRoot: root.path,
      transport: transport,
      trustStore: LauncherUpdateTrustStore([key.publicKey]),
    );
    addTearDown(repository.dispose);
    expect(
      (await repository.checkForUpdate(
        currentVersion: '1.0.0-rc.1',
        channel: LauncherUpdateChannel.beta,
        force: true,
      )).phase,
      LauncherUpdatePhase.available,
    );

    await _publishFixture(transport, key, version: '1.0.0-rc.2');
    final replay = await repository.checkForUpdate(
      currentVersion: '1.0.0-rc.1',
      channel: LauncherUpdateChannel.beta,
      force: true,
    );

    expect(replay.phase, LauncherUpdatePhase.failed);
    expect(replay.message, contains('older signed update'));
  });

  test('reports unsafe update storage as a failed check', () async {
    File(p.join(root.path, 'updates')).writeAsStringSync('collision');
    final repository = LocalLauncherUpdateRepository(
      dataRoot: root.path,
      transport: transport,
      trustStore: LauncherUpdateTrustStore([key.publicKey]),
    );
    addTearDown(repository.dispose);

    final status = await repository.checkForUpdate(
      currentVersion: '1.0.0-rc.1',
      channel: LauncherUpdateChannel.beta,
      force: true,
    );

    expect(status.phase, LauncherUpdatePhase.failed);
    expect(status.message, contains('storage is unsafe'));
    expect(transport.fetched, isEmpty);
  });
}

Future<void> _publishFixture(
  _FakeUpdateTransport transport,
  LauncherUpdateKeyMaterial key, {
  required String version,
  String? signedTag,
  String? signedReleaseUrl,
}) async {
  const archiveHash =
      'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa';
  final tag = 'v$version';
  final payload = utf8.encode(
    '${const JsonEncoder.withIndent('  ').convert({
      r'$schema': 'https://raw.githubusercontent.com/furroxide/TopiaForge/main/schemas/topiaforge.launcher-update-v1.schema.json',
      'formatVersion': 1,
      'product': 'TopiaForge',
      'version': version,
      'tag': signedTag ?? tag,
      'channel': 'beta',
      'minimumUpdaterVersion': '1.0.0-rc.1',
      'releaseUrl': signedReleaseUrl ?? 'https://github.com/furroxide/TopiaForge/releases/tag/$tag',
      'platforms': {
        for (final entry in const {'windows-x64': (asset: 'TopiaForge-windows-x64.zip', layout: 'portable-root'), 'linux-x64': (asset: 'TopiaForge-linux-x64.zip', layout: 'portable-root'), 'macos-universal': (asset: 'TopiaForge-macos-universal.zip', layout: 'app-bundle')}.entries) entry.key: {'assetName': entry.value.asset, 'url': _assetUri(version, entry.value.asset).toString(), 'sha256': archiveHash, 'size': 4096, 'entryCount': 10, 'expandedSize': 8192, 'installLayout': entry.value.layout},
      },
    })}\n',
  );
  final signature = await key.sign(payload);
  final assets = [
    {
      'name': 'topiaforge-update-v1.json',
      'browser_download_url': _assetUri(
        version,
        'topiaforge-update-v1.json',
      ).toString(),
      'size': payload.length,
    },
    {
      'name': 'topiaforge-update-v1.json.sig',
      'browser_download_url': _assetUri(
        version,
        'topiaforge-update-v1.json.sig',
      ).toString(),
      'size': signature.length,
    },
    for (final name in const [
      'TopiaForge-windows-x64.zip',
      'TopiaForge-linux-x64.zip',
      'TopiaForge-macos-universal.zip',
    ])
      {
        'name': name,
        'browser_download_url': _assetUri(version, name).toString(),
        'size': 4096,
      },
  ];
  transport.resources
    ..clear()
    ..[LocalLauncherUpdateRepository.releasesApi] = Uint8List.fromList(
      utf8.encode(
        jsonEncode([
          {
            'tag_name': tag,
            'html_url':
                'https://github.com/furroxide/TopiaForge/releases/tag/$tag',
            'draft': false,
            'prerelease': true,
            'assets': assets,
          },
        ]),
      ),
    )
    ..[_assetUri(version, 'topiaforge-update-v1.json')] = Uint8List.fromList(
      payload,
    )
    ..[_assetUri(version, 'topiaforge-update-v1.json.sig')] =
        Uint8List.fromList(signature);
  transport.fetched.clear();
}

Uri _assetUri(String version, String name) => Uri.parse(
  'https://github.com/furroxide/TopiaForge/releases/download/v$version/$name',
);

final class _FakeUpdateTransport implements LauncherUpdateTransport {
  final Map<Uri, Uint8List> resources = {};
  final List<Uri> fetched = [];

  @override
  Future<Uint8List> fetch(
    Uri uri, {
    required int maxBytes,
    required String label,
  }) async {
    fetched.add(uri);
    final bytes = resources[uri];
    if (bytes == null) throw StateError('Missing fixture: $uri');
    if (bytes.length > maxBytes) throw StateError('Fixture is oversized.');
    return Uint8List.fromList(bytes);
  }

  @override
  Future<LauncherUpdateDownloadResult> download(
    Uri uri, {
    required File partialFile,
    required int expectedSize,
    required String expectedSha256,
    void Function(double progress)? onProgress,
  }) => throw UnimplementedError();

  @override
  void close() {}
}
