part of '../local_developer_repository.dart';

extension LocalDeveloperUnityVpmNetwork on LocalDeveloperRepository {
  String _resolveVpmPackageUrl(String rawUrl, String sourceUrl) {
    final trimmed = rawUrl.trim();
    final sourceUri = Uri.tryParse(sourceUrl.trim());
    final remoteSource = sourceUri?.scheme == 'https';
    if (trimmed.isEmpty) {
      return trimmed;
    }
    if (p.isAbsolute(trimmed)) {
      if (remoteSource) {
        throw StateError('Remote VPM listings cannot reference local paths.');
      }
      return trimmed;
    }
    final uri = Uri.tryParse(trimmed);
    if (uri != null && uri.hasScheme) {
      if (uri.scheme == 'https' &&
          uri.host.isNotEmpty &&
          uri.userInfo.isEmpty) {
        return uri.toString();
      }
      if (uri.scheme == 'file' && !remoteSource) {
        return uri.toString();
      }
      throw StateError('Unsupported or unsafe VPM package URL: $trimmed');
    }

    final source = sourceUrl.trim();
    if (remoteSource) {
      final resolved = sourceUri!.resolve(trimmed);
      if (resolved.scheme != 'https' ||
          resolved.host.isEmpty ||
          resolved.userInfo.isNotEmpty) {
        throw StateError('Remote VPM package URLs must resolve to HTTPS.');
      }
      return resolved.toString();
    }
    final sourcePath = source.startsWith('file://')
        ? Uri.parse(source).toFilePath(windows: Platform.isWindows)
        : source;
    return p.normalize(p.join(p.dirname(sourcePath), trimmed));
  }

  Future<List<int>> _fetchVpmBytes(
    String url, {
    int maxBytes = _maxDeveloperArchiveBytes,
  }) async {
    final trimmed = url.trim();
    final uri = Uri.tryParse(trimmed);
    if (uri != null && uri.scheme == 'https') {
      final fetched = await fetchHttpsBytes(
        uri,
        maxBytes: maxBytes,
        label: 'VPM content',
        totalTimeout: maxBytes > _maxDeveloperCatalogBytes
            ? const Duration(minutes: 10)
            : const Duration(minutes: 2),
      );
      return fetched.bytes;
    }
    if (!_isWindowsPathLike(trimmed) &&
        uri != null &&
        uri.hasScheme &&
        uri.scheme != 'file') {
      throw StateError('Unsupported VPM URL scheme: ${uri.scheme}');
    }
    final path = !_isWindowsPathLike(trimmed) && uri?.scheme == 'file'
        ? uri!.toFilePath(windows: Platform.isWindows)
        : trimmed;
    return _readDeveloperFileBounded(
      File(path),
      maxBytes: maxBytes,
      label: 'VPM content',
    );
  }
}
