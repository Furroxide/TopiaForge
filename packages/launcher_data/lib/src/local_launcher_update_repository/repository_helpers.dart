part of '../local_launcher_update_repository.dart';

extension _LocalLauncherUpdateRepositoryHelpers
    on LocalLauncherUpdateRepository {
  Future<LauncherUpdateCandidate> _verifiedCandidate(
    _GitHubRelease release,
  ) async {
    final payloadAsset = release.asset('topiaforge-update-v1.json');
    final signatureAsset = release.asset('topiaforge-update-v1.json.sig');
    final payloadBytes = await _transport.fetch(
      payloadAsset.url,
      maxBytes: 1024 * 1024,
      label: 'Signed launcher update payload',
    );
    final signatureBytes = await _transport.fetch(
      signatureAsset.url,
      maxBytes: 16 * 1024,
      label: 'Launcher update signature',
    );
    if (payloadAsset.size != payloadBytes.length ||
        signatureAsset.size != signatureBytes.length) {
      throw const FormatException(
        'GitHub update metadata asset sizes do not match.',
      );
    }
    final verified = await _trustStore.verify(
      payloadBytes: payloadBytes,
      signatureBytes: signatureBytes,
    );
    final candidate = LauncherUpdateCandidate.fromVerifiedJson(
      json: verified.payload,
      signingKeyId: verified.keyId,
      payloadSha256: verified.sha256,
    );
    final expectedChannel = release.prerelease
        ? LauncherUpdateChannel.beta
        : LauncherUpdateChannel.release;
    if (candidate.version != release.version ||
        candidate.tag != release.tag ||
        candidate.channel != expectedChannel ||
        candidate.releaseUrl != release.htmlUrl) {
      throw const FormatException(
        'Signed metadata does not match its GitHub release.',
      );
    }
    for (final artifact in candidate.platforms.values) {
      final githubAsset = release.asset(artifact.assetName);
      if (githubAsset.url.toString() != artifact.url ||
          githubAsset.size != artifact.size) {
        throw FormatException(
          'Signed metadata does not match ${artifact.assetName} on GitHub.',
        );
      }
    }
    return candidate;
  }

  void _rejectReplay(
    LauncherUpdateCandidate candidate,
    Map<String, Object?> persisted,
  ) {
    final highest = SemanticVersion.tryParse(
      persisted['highestSeenVersion'] as String? ?? '',
    );
    final target = SemanticVersion.tryParse(candidate.version)!;
    if (highest != null && target.compareTo(highest) < 0) {
      throw const FormatException(
        'GitHub returned an older signed update than previously observed.',
      );
    }
  }

  Map<String, Object?> _readState() {
    for (final file in [_stateFile, File('${_stateFile.path}.previous')]) {
      if (FileSystemEntity.typeSync(file.path, followLinks: false) !=
              FileSystemEntityType.file ||
          file.lengthSync() > 64 * 1024) {
        continue;
      }
      try {
        final decoded = jsonDecode(file.readAsStringSync());
        if (decoded is Map) return Map<String, Object?>.from(decoded);
      } on Object {
        continue;
      }
    }
    return {};
  }

  void _writeState(Map<String, Object?> state) {
    _ensureLauncherUpdateStorage(_dataRoot, _updatesRoot, _transactionsRoot);
    final temporary = File(
      '${_stateFile.path}.tmp-$pid-${Random.secure().nextInt(0x7fffffff)}',
    );
    final previous = File('${_stateFile.path}.previous');
    for (final file in [_stateFile, previous]) {
      final type = FileSystemEntity.typeSync(file.path, followLinks: false);
      if (type != FileSystemEntityType.notFound &&
          type != FileSystemEntityType.file) {
        throw StateError('Launcher update state storage is unsafe.');
      }
    }
    temporary.writeAsStringSync(
      '${const JsonEncoder.withIndent('  ').convert(state)}\n',
      flush: true,
    );
    if (_stateFile.existsSync()) {
      if (previous.existsSync()) previous.deleteSync();
      _stateFile.renameSync(previous.path);
    }
    try {
      temporary.renameSync(_stateFile.path);
    } on Object {
      if (!_stateFile.existsSync() && previous.existsSync()) {
        previous.renameSync(_stateFile.path);
      }
      rethrow;
    } finally {
      if (temporary.existsSync()) temporary.deleteSync();
    }
  }
}
