part of 'launcher_update_index_builder.dart';

abstract interface class GitHubReleaseClient {
  Future<List<GitHubRelease>> listReleases(String repository);

  Future<Stream<List<int>>> openAsset(GitHubAsset asset);
}

class HttpGitHubReleaseClient implements GitHubReleaseClient {
  HttpGitHubReleaseClient({String? token, HttpClient? httpClient})
    : _token = token,
      _httpClient = httpClient ?? HttpClient();

  final String? _token;
  final HttpClient _httpClient;

  @override
  Future<List<GitHubRelease>> listReleases(String repository) async {
    final releases = <GitHubRelease>[];
    for (var page = 1; ; page++) {
      final uri = Uri.https('api.github.com', '/repos/$repository/releases', {
        'per_page': '100',
        'page': '$page',
      });
      final pageJson = await _getJson(uri);
      if (pageJson is! List) {
        throw FormatException('GitHub releases response was not a list.');
      }
      final pageReleases = [
        for (final item in pageJson.whereType<Map>())
          GitHubRelease.fromJson(Map<String, Object?>.from(item)),
      ];
      releases.addAll(pageReleases);
      if (pageReleases.length < 100) {
        return releases;
      }
    }
  }

  @override
  Future<Stream<List<int>>> openAsset(GitHubAsset asset) async {
    final uri = Uri.parse(asset.apiUrl);
    final request = await _httpClient.getUrl(uri);
    _applyHeaders(request, 'application/octet-stream');
    final response = await request.close();
    if (response.statusCode < 200 || response.statusCode >= 300) {
      final body = await utf8.decoder.bind(response).join();
      throw HttpException(
        'GitHub asset download failed with ${response.statusCode}: $body',
        uri: uri,
      );
    }
    return response;
  }

  void close({bool force = false}) => _httpClient.close(force: force);

  Future<Object?> _getJson(Uri uri) async {
    final request = await _httpClient.getUrl(uri);
    _applyHeaders(request, 'application/json');
    final response = await request.close();
    final body = await utf8.decoder.bind(response).join();
    if (response.statusCode < 200 || response.statusCode >= 300) {
      throw HttpException(
        'GitHub API request failed with ${response.statusCode}: $body',
        uri: uri,
      );
    }
    return jsonDecode(body);
  }

  void _applyHeaders(HttpClientRequest request, String accept) {
    request.headers.set(HttpHeaders.acceptHeader, accept);
    request.headers.set(
      HttpHeaders.userAgentHeader,
      'topiaforge-launcher-update-index',
    );
    request.headers.set('X-GitHub-Api-Version', '2022-11-28');
    final token = _token;
    if (token != null && token.trim().isNotEmpty) {
      request.headers.set(HttpHeaders.authorizationHeader, 'Bearer $token');
    }
  }
}

class GitHubRelease {
  const GitHubRelease({
    required this.tagName,
    required this.name,
    required this.body,
    required this.draft,
    required this.prerelease,
    required this.publishedAt,
    required this.assets,
  });

  factory GitHubRelease.fromJson(Map<String, Object?> json) {
    final assetsJson = json['assets'];
    return GitHubRelease(
      tagName: _string(json, 'tag_name'),
      name: json['name'] as String? ?? '',
      body: json['body'] as String? ?? '',
      draft: json['draft'] == true,
      prerelease: json['prerelease'] == true,
      publishedAt: json['published_at'] as String? ?? '',
      assets: assetsJson is List
          ? [
              for (final item in assetsJson.whereType<Map>())
                GitHubAsset.fromJson(Map<String, Object?>.from(item)),
            ]
          : const [],
    );
  }

  final String tagName;
  final String name;
  final String body;
  final bool draft;
  final bool prerelease;
  final String publishedAt;
  final List<GitHubAsset> assets;
}

class GitHubAsset {
  const GitHubAsset({
    required this.name,
    required this.apiUrl,
    required this.browserDownloadUrl,
  });

  factory GitHubAsset.fromJson(Map<String, Object?> json) {
    return GitHubAsset(
      name: _string(json, 'name'),
      apiUrl: _string(json, 'url'),
      browserDownloadUrl: _string(json, 'browser_download_url'),
    );
  }

  final String name;
  final String apiUrl;
  final String browserDownloadUrl;
}

String _string(Map<String, Object?> json, String key) {
  final value = json[key];
  if (value is String && value.isNotEmpty) {
    return value;
  }
  throw FormatException('GitHub response is missing string field "$key".');
}
