part of '../local_launcher_update_repository.dart';

final class _GitHubAsset {
  const _GitHubAsset({
    required this.name,
    required this.url,
    required this.size,
  });

  final String name;
  final Uri url;
  final int size;
}

final class _GitHubRelease {
  const _GitHubRelease({
    required this.tag,
    required this.version,
    required this.htmlUrl,
    required this.draft,
    required this.prerelease,
    required this.assets,
  });

  final String tag;
  final String version;
  final String htmlUrl;
  final bool draft;
  final bool prerelease;
  final Map<String, _GitHubAsset> assets;

  _GitHubAsset asset(String name) =>
      assets[name] ??
      (throw FormatException('GitHub release is missing $name.'));
}

List<_GitHubRelease> _decodeReleaseList(List<int> bytes) {
  final decoded = jsonDecode(utf8.decode(bytes, allowMalformed: false));
  if (decoded is! List || decoded.length > 20) {
    throw const FormatException('GitHub releases response is invalid.');
  }
  return [
    for (final value in decoded)
      _decodeGitHubRelease(Map<String, Object?>.from(value as Map)),
  ];
}

_GitHubRelease _decodeGitHubRelease(Map<String, Object?> json) {
  final tag = json['tag_name'] as String? ?? '';
  final htmlUrl = json['html_url'] as String? ?? '';
  final assetsJson = json['assets'];
  final expectedHtml =
      'https://github.com/furroxide/TopiaForge/releases/tag/$tag';
  if (tag.length > 128 ||
      !tag.startsWith('v') ||
      htmlUrl != expectedHtml ||
      assetsJson is! List ||
      assetsJson.length > 100) {
    throw const FormatException('GitHub release record is invalid.');
  }
  final assets = <String, _GitHubAsset>{};
  for (final value in assetsJson) {
    final asset = Map<String, Object?>.from(value as Map);
    final name = asset['name'] as String? ?? '';
    final url = Uri.tryParse(asset['browser_download_url'] as String? ?? '');
    final size = (asset['size'] as num?)?.toInt() ?? 0;
    if (name.isEmpty ||
        name.length > 256 ||
        url == null ||
        url.scheme != 'https' ||
        url.host != 'github.com' ||
        url.userInfo.isNotEmpty ||
        url.hasQuery ||
        url.hasFragment ||
        size <= 0) {
      throw const FormatException('GitHub release asset is invalid.');
    }
    if (assets.containsKey(name)) {
      throw const FormatException('GitHub release has duplicate assets.');
    }
    assets[name] = _GitHubAsset(name: name, url: url, size: size);
  }
  return _GitHubRelease(
    tag: tag,
    version: tag.substring(1),
    htmlUrl: htmlUrl,
    draft: json['draft'] == true,
    prerelease: json['prerelease'] == true,
    assets: Map.unmodifiable(assets),
  );
}

String _randomHex(int byteCount) {
  final random = Random.secure();
  return [
    for (var index = 0; index < byteCount; index++)
      random.nextInt(256).toRadixString(16).padLeft(2, '0'),
  ].join();
}

final class _UpdateJournalSeed {
  const _UpdateJournalSeed();

  void write(LauncherUpdateTransactionPlan plan) {
    File(plan.journalFile).writeAsStringSync(
      '${jsonEncode({'formatVersion': 1, 'transactionId': plan.transactionId, 'phase': 'planned', 'launchedPid': 0, 'error': ''})}\n',
      flush: true,
    );
  }
}
