part of '../local_launcher_repository.dart';

/// A join's downloads: each package the plan downloads, from the source the
/// plan names, into the package cache.
///
/// - **Bounds.** A body is at most [ModRegistryFormat.maxPackageBytes], and
///   exactly the size its source states when it states one.
/// - **Integrity.** The SHA-256 is computed as the body streams in and must be
///   the one the server pins before the archive is kept; the archive's
///   `topiaforge.mod.json` must then equal the trusted manifest the plan used.
///   Either failure keeps nothing.
/// - **Cancellation** closes the transfer at once and leaves no partial file.
///   Archives already verified stay in the cache, which every install reads
///   by SHA-256, so joining again does not fetch them twice.
/// - **Packages only the host serves** come last, under the same rules, from
///   the host's own endpoint ([_JoinHostDownloads]).
///
/// Failures name the mod and its source and say what to do next. What went
/// wrong underneath goes to the launcher log, never a URL or an address.
extension _JoinContentDownloads on LocalLauncherRepository {
  Future<JoinEvent?> _downloadJoinContent(
    JoinSessionState state, {
    void Function(JoinDownloadProgressed progress)? onProgress,
    CancellationToken? cancellation,
    JoinHostPackageSource? hostPackages,
  }) async {
    final request = state.downloadRequest;
    final plan = state.plan;
    if (request == null || plan == null) return null;
    final items = [
      for (final item in plan.packages)
        if (item.disposition == JoinPackageDisposition.download) item,
    ];
    final hosted = [
      for (final item in plan.packages)
        if (item.disposition == JoinPackageDisposition.downloadFromHost) item,
    ];
    // Developer mode, the acknowledgement and the host's reach are checked
    // before anything is fetched.
    if (hosted.isNotEmpty) {
      final refusal = await _hostDownloadRefusal(
        state,
        hosted,
        hostPackages,
        cancellation,
      );
      if (refusal != null) return refusal;
    }
    final all = [...items, ...hosted];
    final progress = _JoinDownloadProgress(all, onProgress);
    await _sweepStaleCacheDownloads();
    for (var index = 0; index < all.length; index++) {
      if (cancellation?.isCancelled ?? false) return null;
      void update(int received, int? total) =>
          progress.update(index, received, total);
      try {
        final failure = index < items.length
            ? await _obtainJoinArchive(items[index], update, cancellation)
            : await _obtainHostArchive(
                state,
                all[index],
                hostPackages!,
                update,
                cancellation,
              );
        if (failure != null) return failure;
      } on OperationCancelledException {
        return null;
      } on StateError {
        return JoinFailed(
          JoinFailureCode.downloadFailed,
          detail:
              'The package cache could not be used. Close other TopiaForge '
              'install operations, check free disk space and cache folder '
              'permissions, then join again.',
        );
      }
      progress.complete(index);
    }
    return const JoinDownloadsCompleted();
  }

  /// Where the cache keeps the archive with [sha256].
  File _joinCacheFile(String sha256) =>
      File(p.join(_packageCache.path, '$sha256.topiaforgemod'));

  /// Puts [item]'s exact archive in the cache, verified. Null when it is
  /// there; the failure otherwise. Throws [OperationCancelledException]
  /// when cancelled.
  Future<JoinFailed?> _obtainJoinArchive(
    JoinPlanPackage item,
    SecureHttpProgress onProgress,
    CancellationToken? cancellation,
  ) async {
    final package = item.package;
    final name = _joinModName(item);
    final source = item.resolution.sourceName.isEmpty
        ? 'its source'
        : item.resolution.sourceName;
    final stated = item.resolution.sizeBytes;
    final cached = _joinCacheFile(package.sha256);
    final kept = await _cachedArchiveLength(cached, package.sha256, stated);
    if (kept != null) {
      onProgress(kept, kept);
    } else {
      final temp = File(
        '${cached.path}.$pid-${DateTime.now().microsecondsSinceEpoch}.tmp',
      );
      final ({String sha256, int length}) fetched;
      try {
        await cached.parent.create(recursive: true);
        fetched = await _fetchJoinArchive(item, temp, onProgress, cancellation);
      } on OperationCancelledException {
        await _deleteFileBestEffort(temp);
        rethrow;
      } on SecureHttpDownloadException catch (error) {
        await _deleteFileBestEffort(temp);
        await _logJoinDownload(package, error.message);
        return _downloadRefusal(error, name, source, stated);
      } on Object catch (error) {
        await _deleteFileBestEffort(temp);
        if (cancellation?.isCancelled ?? false) {
          throw const OperationCancelledException();
        }
        // Network errors can name a host or an address, so only their kind
        // is logged.
        await _logJoinDownload(package, '${error.runtimeType}');
        return JoinFailed(
          JoinFailureCode.downloadFailed,
          detail:
              '$name could not be downloaded from $source. Check your internet '
              'connection, then join again.',
        );
      }
      if (fetched.sha256 != package.sha256) {
        await _deleteFileBestEffort(temp);
        return JoinFailed(
          JoinFailureCode.integrityFailed,
          detail:
              'The copy of $name from $source is not the one the server runs, '
              'so nothing was installed. Try again later; if this keeps '
              'happening, $source has a different file than the server.',
        );
      }
      if (!await _keepVerifiedDownload(temp, cached, package.sha256)) {
        return JoinFailed(
          JoinFailureCode.downloadFailed,
          detail:
              '$name was downloaded but could not be saved. Check that there '
              'is free disk space, then join again.',
        );
      }
    }
    final mismatch = await _joinManifestMismatch(cached, item, name, source);
    if (mismatch == null) return null;
    await _deleteFileBestEffort(cached);
    return JoinFailed(JoinFailureCode.manifestMismatch, detail: mismatch);
  }

  /// Logs why [package] did not download. [reason] can repeat what an
  /// archive's author wrote, at any length, so only what the log keeps of
  /// it is read.
  Future<void> _logJoinDownload(DescriptorPackage package, String reason) =>
      _appendLauncherLogBestEffort(
        withoutAddresses(
          'Join download of ${package.id} ${package.version} failed: $reason',
          maxLength: _maxLauncherLogMessageCharacters,
        ),
      );

  /// Streams [item]'s archive from its source into [temp], hashing it.
  Future<({String sha256, int length})> _fetchJoinArchive(
    JoinPlanPackage item,
    File temp,
    SecureHttpProgress onProgress,
    CancellationToken? cancellation,
  ) async {
    final stated = item.resolution.sizeBytes;
    final uri = Uri.tryParse(item.resolution.downloadUrl);
    if (uri != null && uri.scheme.toLowerCase() == 'https') {
      final result = await downloadHttpsToFile(
        uri,
        temp,
        maxBytes: ModRegistryFormat.maxPackageBytes,
        expectedBytes: stated,
        label: 'The package download',
        onProgress: onProgress,
        cancellation: cancellation,
      );
      return (sha256: result.sha256, length: result.length);
    }
    if (uri != null && uri.scheme == 'file') {
      return _copyLocalArchive(
        File(uri.toFilePath(windows: Platform.isWindows)),
        temp,
        stated,
        onProgress,
        cancellation,
      );
    }
    throw StateError('The package source gives no URL a join can use.');
  }

  /// Copies a local source's archive into [temp], hashing it, under the same
  /// bounds as a download.
  Future<({String sha256, int length})> _copyLocalArchive(
    File source,
    File temp,
    int? stated,
    SecureHttpProgress onProgress,
    CancellationToken? cancellation,
  ) async {
    if (FileSystemEntity.typeSync(source.path, followLinks: false) !=
        FileSystemEntityType.file) {
      throw StateError('The package file is missing.');
    }
    final length = await source.length();
    final limit = stated ?? ModRegistryFormat.maxPackageBytes;
    if (length > limit || (stated != null && length != stated)) {
      throw SecureHttpDownloadException(
        stated == null
            ? SecureHttpDownloadProblem.tooLarge
            : SecureHttpDownloadProblem.unexpectedLength,
        'The package file is $length bytes.',
      );
    }
    final digest = _CapturedDigest();
    final hashing = sha256.startChunkedConversion(digest);
    final output = await temp.open(mode: FileMode.write);
    var copied = 0;
    try {
      await for (final chunk in source.openRead()) {
        cancellation?.throwIfCancelled();
        if (chunk.length > limit - copied) {
          throw const SecureHttpDownloadException(
            SecureHttpDownloadProblem.tooLarge,
            'The package file grew while it was copied.',
          );
        }
        hashing.add(chunk);
        await output.writeFrom(chunk);
        copied += chunk.length;
        onProgress(copied, stated ?? length);
      }
      await output.flush();
    } finally {
      await output.close();
    }
    hashing.close();
    return (sha256: digest.value.toString(), length: copied);
  }

  /// The length of the exact archive when the cache already holds it, else
  /// null. A cached file that is not it is deleted, so it is fetched afresh.
  Future<int?> _cachedArchiveLength(
    File cached,
    String expectedSha256,
    int? stated,
  ) => _withPackageCacheLock(() async {
    await _retainPackageCache(expectedSha256);
    return _cachedArchiveLengthLocked(cached, expectedSha256, stated);
  });

  Future<int?> _cachedArchiveLengthLocked(
    File cached,
    String expectedSha256,
    int? stated,
  ) async {
    if (FileSystemEntity.typeSync(cached.path, followLinks: false) !=
        FileSystemEntityType.file) {
      return null;
    }
    try {
      final length = await cached.length();
      if (length <= ModRegistryFormat.maxPackageBytes &&
          (stated == null || length == stated) &&
          (await sha256.bind(cached.openRead()).first).toString() ==
              expectedSha256) {
        return length;
      }
    } on FileSystemException {
      // Unreadable just now: fetched afresh below.
    }
    await _deleteFileBestEffort(cached);
    return null;
  }

  /// Moves a verified download into the cache. Another writer may have put
  /// the same archive there meanwhile, which is as good. A cache lease that
  /// another process holds, or a retention that fails, throws [StateError],
  /// so the join says what to close rather than to free disk space.
  Future<bool> _keepVerifiedDownload(
    File temp,
    File cached,
    String sha256Hex,
  ) async {
    try {
      return await _withPackageCacheLock(() async {
        await _retainPackageCache(sha256Hex);
        return _keepVerifiedDownloadLocked(temp, cached, sha256Hex);
      });
    } on StateError {
      await _deleteFileBestEffort(temp);
      rethrow;
    } on Object {
      await _deleteFileBestEffort(temp);
      return false;
    }
  }

  Future<bool> _keepVerifiedDownloadLocked(
    File temp,
    File cached,
    String sha256Hex,
  ) async {
    try {
      await temp.rename(cached.path);
      return true;
    } on FileSystemException {
      if (await _cachedArchiveLength(cached, sha256Hex, null) != null) {
        await _deleteFileBestEffort(temp);
        return true;
      }
      try {
        await temp.rename(cached.path);
        return true;
      } on FileSystemException {
        await _deleteFileBestEffort(temp);
        return false;
      }
    }
  }

  /// Why the cached archive's manifest is not the trusted one, for the
  /// player, or null when it is.
  Future<String?> _joinManifestMismatch(
    File cached,
    JoinPlanPackage item,
    String name,
    String source,
  ) async {
    final _PackageReadResult archive;
    try {
      archive = await _readPackage(
        cached.path,
        expectedSha256: item.package.sha256,
      );
    } on Object catch (error) {
      await _logJoinDownload(item.package, _failureText(error));
      return 'The copy of $name from $source is not a mod package the '
          'launcher can read, so nothing was installed. Try again later, or '
          'ask the host about this mod.';
    }
    if (_canonicalPackageManifest(archive.manifest) !=
        _canonicalPackageManifest(item.manifest!)) {
      return 'The copy of $name from $source does not match its listing in '
          'that source, so nothing was installed. The source may be '
          'updating; try again later.';
    }
    return null;
  }

  /// Deletes downloads into the cache that a stopped process left, once
  /// they are an hour old. Tidying only.
  Future<void> _sweepStaleCacheDownloads() async {
    final pattern = RegExp(
      r'^[0-9a-f]{64}\.topiaforgemod\.[0-9]+-[0-9]+\.tmp$',
    );
    try {
      final now = DateTime.now();
      await for (final entity in _packageCache.list(followLinks: false)) {
        if (entity is! File || !pattern.hasMatch(p.basename(entity.path))) {
          continue;
        }
        final stat = await entity.stat();
        if (now.difference(stat.modified) >= const Duration(hours: 1)) {
          await _deleteFileBestEffort(entity);
        }
      }
    } on FileSystemException {
      // No cache yet, or it cannot be listed just now.
    }
  }
}

/// A plan package's name for the player: the trusted manifest's display
/// name and the version, or the id when there is no manifest, as for a
/// package only the host serves.
String _joinModName(JoinPlanPackage item) =>
    '${_joinModTitle(item)} ${item.package.version}';

/// A plan package's mod for the player, at whatever version: the trusted
/// manifest's display name, or the id when there is no manifest.
String _joinModTitle(JoinPlanPackage item) {
  final name = item.manifest?.name.trim() ?? '';
  return name.isEmpty ? item.package.id : name;
}

/// What a refused download means for the join, for the player.
JoinFailed _downloadRefusal(
  SecureHttpDownloadException error,
  String name,
  String source,
  int? stated,
) => switch (error.problem) {
  SecureHttpDownloadProblem.status => JoinFailed(
    JoinFailureCode.downloadFailed,
    detail:
        '$source could not provide $name just now (it answered HTTP '
        '${error.statusCode}). Try joining again later.',
  ),
  SecureHttpDownloadProblem.tooLarge when stated == null => JoinFailed(
    JoinFailureCode.downloadFailed,
    detail:
        '$name from $source is larger than a mod may be '
        '(${ModRegistryFormat.maxPackageBytes ~/ (1024 * 1024)} MB), so it was '
        'not downloaded. Ask the host about this mod.',
  ),
  SecureHttpDownloadProblem.tooLarge ||
  SecureHttpDownloadProblem.unexpectedLength => JoinFailed(
    JoinFailureCode.integrityFailed,
    detail:
        'The copy of $name from $source is not the size the source lists, so '
        'it is not the one the server runs and nothing was installed. Try '
        'again later.',
  ),
  SecureHttpDownloadProblem.endedEarly => JoinFailed(
    JoinFailureCode.downloadFailed,
    detail:
        'The download of $name from $source was cut off. Join again to retry '
        'it.',
  ),
};

/// Download progress across every package of a join, as one total.
final class _JoinDownloadProgress {
  _JoinDownloadProgress(List<JoinPlanPackage> items, this._report)
    : _stated = [for (final item in items) item.resolution.sizeBytes],
      _announced = List.filled(items.length, null),
      _received = List.filled(items.length, 0);

  /// A report at most this often while bytes arrive, and at each package's
  /// end.
  static const reportEvery = 256 * 1024;

  final void Function(JoinDownloadProgressed progress)? _report;
  final List<int?> _stated;
  final List<int?> _announced;
  final List<int> _received;
  var _reported = 0;

  void update(int index, int received, int? total) {
    _received[index] = received;
    _announced[index] ??= total;
    if (_done - _reported >= reportEvery) _emit();
  }

  void complete(int index) {
    _stated[index] ??= _received[index];
    _emit();
  }

  int get _done => _received.fold(0, (total, value) => total + value);

  void _emit() {
    final done = _done;
    var total = 0;
    for (var index = 0; index < _received.length; index++) {
      total += max(_stated[index] ?? _announced[index] ?? 0, _received[index]);
    }
    _reported = done;
    _report?.call(JoinDownloadProgressed(bytesDone: done, bytesTotal: total));
  }
}

final class _CapturedDigest implements Sink<Digest> {
  Digest? _value;

  Digest get value => _value!;

  @override
  void add(Digest data) => _value = data;

  @override
  void close() {}
}
