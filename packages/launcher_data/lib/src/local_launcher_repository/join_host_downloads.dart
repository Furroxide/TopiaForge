part of '../local_launcher_repository.dart';

/// A join's packages only the host serves, in developer mode: fetched last,
/// from the host's own endpoint through the [JoinHostPackageSource] the
/// multiplayer facade hands over, and checked as every join download is.
///
/// - **Gates, checked here and not only in the domain.** Developer mode, as
///   the launcher's settings say now, and the player's acknowledgement
///   ([JoinSessionState.hostServedAcknowledged]), before anything is fetched;
///   then whether the host can be reached at all.
/// - **Integrity.** The SHA-256 computed as the archive streamed must be the
///   one the server's signed descriptor pins. No source vouches for these
///   archives' manifests, so each must name exactly the mod and version the
///   descriptor claims. Verified archives join the package cache by SHA-256,
///   so joining again fetches nothing twice.
/// - **Failures** name the mod and say what to do next. They and the
///   launcher log name no address.
extension _JoinHostDownloads on LocalLauncherRepository {
  /// Why the host's packages, [hosted], cannot be fetched at all; null when
  /// they can, or when [cancellation] stopped the check.
  Future<JoinFailed?> _hostDownloadRefusal(
    JoinSessionState state,
    List<JoinPlanPackage> hosted,
    JoinHostPackageSource? source,
    CancellationToken? cancellation,
  ) async {
    final names = hosted.map(_joinModName).join(', ');
    final refusal = await _hostServedRefusal(state, names);
    if (refusal != null) {
      return JoinFailed(
        JoinFailureCode.hostDownloadUnsupported,
        detail: '$refusal Nothing was downloaded.',
      );
    }
    if (source == null) {
      return JoinFailed(
        JoinFailureCode.hostDownloadUnsupported,
        detail:
            'Only the host has $names, and this join was given no way to '
            'reach the host for them. Ask the host to publish them to a mod '
            'source you use. Nothing was downloaded.',
      );
    }
    final problem = await source.prepare(cancellation: cancellation);
    if (problem == null || (cancellation?.isCancelled ?? false)) return null;
    return _hostDownloadFailure(problem, names, before: true);
  }

  /// Why the launcher may not take packages from a host for this join, for
  /// the player, or null when it may: developer mode must be on now, and the
  /// player must have acknowledged them at consent.
  Future<String?> _hostServedRefusal(
    JoinSessionState state,
    String names,
  ) async {
    if (!await _developerModeOn()) {
      return 'Only the host has $names, and taking mods from a host needs '
          'developer mode, which is off. Turn it on in Settings and join '
          'again, or ask the host to publish them to a mod source you use.';
    }
    if (!state.hostServedAcknowledged) {
      return 'Only the host has $names, and taking mods from a host needs '
          "your agreement in the join's developer-mode section. Join again "
          'and agree to it, or ask the host to publish them to a mod source '
          'you use.';
    }
    return null;
  }

  /// Whether developer mode is on in the launcher's settings now. Settings
  /// that cannot be read never count as on.
  Future<bool> _developerModeOn() async {
    try {
      return (await _loadSettings())['developerMode'] == true;
    } on Object {
      return false;
    }
  }

  /// Puts the host's archive of [item] in the cache, verified. Null when it
  /// is there; the failure otherwise. Throws [OperationCancelledException]
  /// when cancelled.
  Future<JoinFailed?> _obtainHostArchive(
    JoinPlanPackage item,
    JoinHostPackageSource source,
    SecureHttpProgress onProgress,
    CancellationToken? cancellation,
  ) async {
    final package = item.package;
    final name = _joinModName(item);
    final cached = _joinCacheFile(package.sha256);
    final kept = await _cachedArchiveLength(cached, package.sha256, null);
    if (kept != null) {
      onProgress(kept, kept);
    } else {
      final temp = File(
        '${cached.path}.$pid-${DateTime.now().microsecondsSinceEpoch}.tmp',
      );
      try {
        await cached.parent.create(recursive: true);
      } on FileSystemException {
        return _hostDownloadFailure(JoinHostPackageProblem.notSaved, name);
      }
      final result = await source.download(
        package,
        temp.path,
        onProgress: onProgress,
        cancellation: cancellation,
      );
      switch (result) {
        case JoinHostPackageFailed(:final problem):
          // The source leaves nothing at the temporary path.
          if (problem == JoinHostPackageProblem.cancelled ||
              (cancellation?.isCancelled ?? false)) {
            throw const OperationCancelledException();
          }
          await _logJoinDownload(package, 'the host: ${problem.name}');
          return _hostDownloadFailure(problem, name);
        case JoinHostPackageDownloaded(:final sha256):
          if (sha256 != package.sha256) {
            await _deleteFileBestEffort(temp);
            return JoinFailed(
              JoinFailureCode.integrityFailed,
              detail:
                  'The copy of $name the host sent is not the one its '
                  'session pins, so nothing was kept. Ask the host about this '
                  'mod, then join again.',
            );
          }
          if (!await _keepVerifiedDownload(temp, cached, package.sha256)) {
            return _hostDownloadFailure(JoinHostPackageProblem.notSaved, name);
          }
      }
    }
    final mismatch = await _hostManifestMismatch(cached, item, name);
    if (mismatch == null) return null;
    await _deleteFileBestEffort(cached);
    return JoinFailed(JoinFailureCode.manifestMismatch, detail: mismatch);
  }

  /// Why the cached archive of [item] is not the package the descriptor
  /// claims, for the player, or null when it is.
  Future<String?> _hostManifestMismatch(
    File cached,
    JoinPlanPackage item,
    String name,
  ) async {
    final ModManifest manifest;
    try {
      manifest = (await _readPackage(
        cached.path,
        expectedSha256: item.package.sha256,
      )).manifest;
    } on Object catch (error) {
      await _logJoinDownload(item.package, _failureText(error));
      return 'The copy of $name the host sent is not a mod package the '
          'launcher can read, so nothing was kept. Ask the host about this '
          'mod.';
    }
    if (_namesHostPackage(manifest, item.package)) return null;
    return 'The copy of $name the host sent holds another mod or version '
        'than its session lists, so nothing was kept. Ask the host about '
        'this mod.';
  }
}

/// Whether [manifest] is the package [package] claims: the same id, as the
/// planner compares ids, and exactly the same version.
bool _namesHostPackage(ModManifest manifest, DescriptorPackage package) =>
    manifest.id.toLowerCase() == package.id.toLowerCase() &&
    manifest.version == package.version;

/// What a host-served download that failed means for the join, for the
/// player. [names] names the mods; [before] says nothing was fetched yet.
JoinFailed _hostDownloadFailure(
  JoinHostPackageProblem problem,
  String names, {
  bool before = false,
}) {
  final nothing = before ? ' Nothing was downloaded.' : '';
  return switch (problem) {
    JoinHostPackageProblem.hostUnknown => JoinFailed(
      JoinFailureCode.hostDownloadUnsupported,
      detail:
          'Only the host has $names, and a host shares mods only over a '
          'direct or local-network connection, while this join reaches it '
          'through the TopiaForge service. Join it on your local network, or '
          'ask the host to publish them to a mod source you use.$nothing',
    ),
    JoinHostPackageProblem.directNotAllowed => JoinFailed(
      JoinFailureCode.directNotAllowed,
      detail:
          'Only the host has $names, and reaching the host now would show it '
          'your IP address, which Hide my IP keeps hidden. A host shares mods '
          'only over a direct or local-network connection: join it on your '
          'local network, or allow a direct connection for this session.'
          '$nothing',
    ),
    JoinHostPackageProblem.notHeard => JoinFailed(
      JoinFailureCode.unreachable,
      detail:
          'Only the host has $names, and the host is no longer heard on your '
          'local network. Check that it is still running and that LAN '
          'discovery is on in Multiplayer settings, then join again.$nothing',
    ),
    JoinHostPackageProblem.unreachable => JoinFailed(
      JoinFailureCode.unreachable,
      detail:
          'The host could not be reached to download $names. Check that it '
          'is still running, then join again.$nothing',
    ),
    JoinHostPackageProblem.stalled => JoinFailed(
      JoinFailureCode.unreachable,
      detail:
          'The host stopped sending $names, so the download was stopped. '
          'Check the connection to the host, then join again.',
    ),
    JoinHostPackageProblem.timedOut => JoinFailed(
      JoinFailureCode.downloadFailed,
      detail:
          '$names took longer to arrive than the two minutes a host gives one '
          'download, so it was stopped. Check the connection to the host, '
          'then join again.',
    ),
    JoinHostPackageProblem.notServed => JoinFailed(
      JoinFailureCode.downloadFailed,
      detail:
          'The host no longer has $names, or could not open it. Ask the host '
          'to check that mod, then join again.',
    ),
    JoinHostPackageProblem.busy => JoinFailed(
      JoinFailureCode.downloadFailed,
      detail:
          'The host was too busy to send $names. Wait a moment, then join '
          'again.',
    ),
    JoinHostPackageProblem.refused => JoinFailed(
      JoinFailureCode.downloadFailed,
      detail:
          'The host refused to send $names. Ask the host to check that it '
          'serves its mods, then join again.',
    ),
    JoinHostPackageProblem.malformed => JoinFailed(
      JoinFailureCode.downloadFailed,
      detail:
          "The host's answer for $names was not one this launcher can read, "
          'so nothing was kept. Make sure the host and you run the same '
          'TopiaForge version, then join again.',
    ),
    JoinHostPackageProblem.tooLarge => JoinFailed(
      JoinFailureCode.downloadFailed,
      detail:
          '$names from the host is larger than a mod may be '
          '(${ModRegistryFormat.maxPackageBytes ~/ (1024 * 1024)} MB), so it '
          'was not downloaded. Ask the host about this mod.',
    ),
    JoinHostPackageProblem.endedEarly => JoinFailed(
      JoinFailureCode.downloadFailed,
      detail:
          'The download of $names from the host was cut off. Join again to '
          'retry it.',
    ),
    JoinHostPackageProblem.notSaved => JoinFailed(
      JoinFailureCode.downloadFailed,
      detail:
          '$names could not be saved. Check that there is free disk space, '
          'then join again.',
    ),
    // A cancelled join hears its own cancel; this keeps the switch whole.
    JoinHostPackageProblem.cancelled => JoinFailed(
      JoinFailureCode.downloadFailed,
      detail: 'The download of $names was stopped.',
    ),
  };
}
