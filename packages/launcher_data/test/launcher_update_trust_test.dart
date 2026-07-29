import 'dart:convert';
import 'dart:io';

import 'package:launcher_data/launcher_data.dart';
import 'package:launcher_domain/launcher_domain.dart';
import 'package:test/test.dart';

void main() {
  test('Ed25519 trust verifies the exact payload bytes and key id', () async {
    final key = await LauncherUpdateKeyMaterial.generate();
    final payload = utf8.encode('{"formatVersion":1,"value":"exact"}\n');
    final signature = await key.sign(payload);
    final trust = LauncherUpdateTrustStore([key.publicKey]);

    final verified = await trust.verify(
      payloadBytes: payload,
      signatureBytes: signature,
    );

    expect(verified.keyId, key.publicKey.id);
    expect(verified.payload['value'], 'exact');
    expect(verified.sha256, hasLength(64));
  });

  test('tampering, wrong keys, and malformed sidecars fail closed', () async {
    final signer = await LauncherUpdateKeyMaterial.generate();
    final wrongKey = await LauncherUpdateKeyMaterial.generate();
    final payload = utf8.encode('{"formatVersion":1,"value":"trusted"}\n');
    final signature = await signer.sign(payload);

    await expectLater(
      LauncherUpdateTrustStore([signer.publicKey]).verify(
        payloadBytes: [...payload]..[payload.length - 3] ^= 1,
        signatureBytes: signature,
      ),
      throwsFormatException,
    );
    await expectLater(
      LauncherUpdateTrustStore([
        wrongKey.publicKey,
      ]).verify(payloadBytes: payload, signatureBytes: signature),
      throwsFormatException,
    );
    await expectLater(
      LauncherUpdateTrustStore([signer.publicKey]).verify(
        payloadBytes: payload,
        signatureBytes: utf8.encode('{"formatVersion":1}\n'),
      ),
      throwsFormatException,
    );
  });

  test('candidate rejects downgrade, wrong channel, and old updater', () async {
    final candidate = _candidate(
      version: '1.1.0-rc.1',
      channel: 'beta',
      minimumUpdaterVersion: '1.0.0-rc.1',
    );

    expect(
      candidate.isEligibleFor(
        currentVersion: '1.0.0-rc.1',
        requestedChannel: LauncherUpdateChannel.beta,
      ),
      isTrue,
    );
    expect(
      candidate.isEligibleFor(
        currentVersion: '1.1.0-rc.1',
        requestedChannel: LauncherUpdateChannel.beta,
      ),
      isFalse,
    );
    expect(
      candidate.isEligibleFor(
        currentVersion: '1.0.0-rc.1',
        requestedChannel: LauncherUpdateChannel.release,
      ),
      isFalse,
    );
    expect(
      _candidate(
        version: '2.0.0',
        channel: 'release',
        minimumUpdaterVersion: '1.1.0',
      ).isEligibleFor(
        currentVersion: '1.0.0-rc.1',
        requestedChannel: LauncherUpdateChannel.beta,
      ),
      isFalse,
    );
  });

  test('candidate rejects unknown channels and payload fields', () {
    expect(
      () => _candidate(
        version: '1.0.0-rc.2',
        channel: 'nightly',
        minimumUpdaterVersion: '1.0.0-rc.1',
      ),
      throwsFormatException,
    );
    expect(
      () => _candidate(
        version: '1.0.0-rc.2',
        channel: 'beta',
        minimumUpdaterVersion: '1.0.0-rc.1',
        extraFields: const {'unexpected': true},
      ),
      throwsFormatException,
    );
  });

  test('embedded trust matches the checked-in public key catalog', () {
    final root = _repositoryRoot();
    final decoded = Map<String, Object?>.from(
      jsonDecode(
            File(
              '${root.path}${Platform.pathSeparator}release'
              '${Platform.pathSeparator}update-keys.json',
            ).readAsStringSync(),
          )
          as Map,
    );
    final checkedIn = LauncherUpdateTrustStore.fromJson(decoded);

    expect(LauncherUpdateTrustStore.embedded().keyIds, checkedIn.keyIds);
  });
}

Directory _repositoryRoot() {
  var current = Directory.current.absolute;
  while (true) {
    if (File(
      '${current.path}${Platform.pathSeparator}release'
      '${Platform.pathSeparator}update-keys.json',
    ).existsSync()) {
      return current;
    }
    final parent = current.parent;
    if (parent.path == current.path) {
      throw StateError('Repository root was not found.');
    }
    current = parent;
  }
}

LauncherUpdateCandidate _candidate({
  required String version,
  required String channel,
  required String minimumUpdaterVersion,
  Map<String, Object?> extraFields = const {},
}) {
  const hashes =
      'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa';
  return LauncherUpdateCandidate.fromVerifiedJson(
    signingKeyId: 'ed25519:0123456789abcdef',
    payloadSha256: hashes,
    json: {
      r'$schema':
          'https://raw.githubusercontent.com/furroxide/TopiaForge/main/'
          'schemas/topiaforge.launcher-update-v1.schema.json',
      'formatVersion': 1,
      'product': 'TopiaForge',
      'version': version,
      'tag': 'v$version',
      'channel': channel,
      'minimumUpdaterVersion': minimumUpdaterVersion,
      'releaseUrl':
          'https://github.com/furroxide/TopiaForge/releases/tag/v$version',
      ...extraFields,
      'platforms': {
        for (final entry in const {
          'windows-x64': (
            asset: 'TopiaForge-windows-x64.zip',
            layout: 'portable-root',
          ),
          'linux-x64': (
            asset: 'TopiaForge-linux-x64.zip',
            layout: 'portable-root',
          ),
          'macos-universal': (
            asset: 'TopiaForge-macos-universal.zip',
            layout: 'app-bundle',
          ),
        }.entries)
          entry.key: {
            'assetName': entry.value.asset,
            'url':
                'https://github.com/furroxide/TopiaForge/releases/download/'
                'v$version/${entry.value.asset}',
            'sha256': hashes,
            'size': 1024,
            'entryCount': 2,
            'expandedSize': 2048,
            'installLayout': entry.value.layout,
          },
      },
    },
  );
}
