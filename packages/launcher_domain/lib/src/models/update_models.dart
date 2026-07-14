part of '../models.dart';

enum LauncherUpdateChannel {
  release,
  beta,
  nightly;

  static LauncherUpdateChannel fromName(String? value) {
    final normalized = (value ?? '').trim().toLowerCase();
    return switch (normalized) {
      'stable' || 'release' => LauncherUpdateChannel.release,
      'beta' || 'preview' || 'prerelease' => LauncherUpdateChannel.beta,
      'nightly' || 'canary' || 'dev' => LauncherUpdateChannel.nightly,
      _ => LauncherUpdateChannel.release,
    };
  }
}

class LauncherUpdateSettings {
  const LauncherUpdateSettings({
    this.enabled = false,
    this.checkAutomatically = true,
    this.channel = LauncherUpdateChannel.release,
    String? manualReleasesUrl,
    @Deprecated('Use manualReleasesUrl') String? appArchiveUrl,
  }) : manualReleasesUrl =
           manualReleasesUrl ?? appArchiveUrl ?? defaultManualReleasesUrl;

  static const defaultManualReleasesUrl =
      'https://furroxide.github.io/quantum-works/manual-releases.json';

  @Deprecated('Use defaultManualReleasesUrl')
  static const defaultAppArchiveUrl = defaultManualReleasesUrl;

  final bool enabled;
  final bool checkAutomatically;
  final LauncherUpdateChannel channel;
  final String manualReleasesUrl;

  @Deprecated('Use manualReleasesUrl')
  String get appArchiveUrl => manualReleasesUrl;

  factory LauncherUpdateSettings.fromJson(Map<String, Object?> json) {
    final configuredUrl =
        (json['manualReleasesUrl'] as String?)?.trim() ??
        (json['appArchiveUrl'] as String?)?.trim() ??
        '';
    return LauncherUpdateSettings(
      // The previous updater accepted unsigned metadata and did not expose
      // complete network/extraction bounds. Persisted opt-ins are therefore
      // migrated to a fail-closed manual-update state.
      enabled: false,
      checkAutomatically: (json['checkAutomatically'] as bool?) ?? true,
      channel: LauncherUpdateChannel.fromName(json['channel'] as String?),
      manualReleasesUrl: _isTrustedPublicHttpsUrl(configuredUrl)
          ? configuredUrl
          : defaultManualReleasesUrl,
    );
  }

  Map<String, Object?> toJson() => {
    'enabled': enabled,
    'checkAutomatically': checkAutomatically,
    'channel': channel.name,
    'manualReleasesUrl': manualReleasesUrl,
  };

  LauncherUpdateSettings copyWith({
    bool? enabled,
    bool? checkAutomatically,
    LauncherUpdateChannel? channel,
    String? manualReleasesUrl,
    @Deprecated('Use manualReleasesUrl') String? appArchiveUrl,
  }) {
    return LauncherUpdateSettings(
      enabled: enabled ?? this.enabled,
      checkAutomatically: checkAutomatically ?? this.checkAutomatically,
      channel: channel ?? this.channel,
      manualReleasesUrl:
          manualReleasesUrl ?? appArchiveUrl ?? this.manualReleasesUrl,
    );
  }
}

class ManualReleaseArtifact {
  const ManualReleaseArtifact({
    required this.url,
    required this.sha256,
    required this.size,
  });

  final String url;
  final String sha256;
  final int size;

  factory ManualReleaseArtifact.fromJson(Map<String, Object?> json) =>
      ManualReleaseArtifact(
        url:
            (json['url'] as String?) ??
            (json['artifactUrl'] as String?) ??
            (json['artifact'] as String?) ??
            '',
        sha256: (json['sha256'] as String?) ?? '',
        size: (json['size'] as num?)?.toInt() ?? 0,
      );

  Map<String, Object?> toJson() => {'url': url, 'sha256': sha256, 'size': size};

  bool get isValid =>
      _isTrustedPublicHttpsUrl(url) &&
      RegExp(r'^[0-9a-fA-F]{64}$').hasMatch(sha256) &&
      size > 0;
}

class ManualReleaseCatalog {
  const ManualReleaseCatalog({
    required this.formatVersion,
    required this.manualOnly,
    required this.releaseUrl,
    required this.platforms,
  });

  final int formatVersion;
  final bool manualOnly;
  final String releaseUrl;
  final Map<String, ManualReleaseArtifact> platforms;

  factory ManualReleaseCatalog.fromJson(Map<String, Object?> json) {
    final rawPlatforms = json['platforms'];
    return ManualReleaseCatalog(
      formatVersion: (json['formatVersion'] as num?)?.toInt() ?? 0,
      manualOnly: json['manualOnly'] == true,
      releaseUrl:
          (json['releaseUrl'] as String?) ?? (json['release'] as String?) ?? '',
      platforms: rawPlatforms is Map
          ? Map.unmodifiable({
              for (final entry in rawPlatforms.entries)
                if (entry.value is Map)
                  entry.key.toString(): ManualReleaseArtifact.fromJson(
                    (entry.value as Map).map(
                      (key, value) => MapEntry(key.toString(), value),
                    ),
                  ),
            })
          : const {},
    );
  }

  Map<String, Object?> toJson() => {
    'formatVersion': formatVersion,
    'manualOnly': manualOnly,
    'releaseUrl': releaseUrl,
    'platforms': {
      for (final entry in platforms.entries) entry.key: entry.value.toJson(),
    },
  };

  bool get isValid =>
      formatVersion == 1 &&
      manualOnly &&
      _isTrustedPublicHttpsUrl(releaseUrl) &&
      platforms.isNotEmpty &&
      platforms.values.every((artifact) => artifact.isValid);
}

bool _isTrustedPublicHttpsUrl(String value) {
  if (value.length > 4096) {
    return false;
  }
  final uri = Uri.tryParse(value);
  return uri != null &&
      uri.scheme == 'https' &&
      uri.host.isNotEmpty &&
      uri.userInfo.isEmpty &&
      !uri.hasQuery &&
      !uri.hasFragment;
}
