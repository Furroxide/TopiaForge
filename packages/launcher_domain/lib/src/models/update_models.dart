part of '../models.dart';

enum LauncherUpdateChannel {
  release,
  beta,
  nightly;

  static LauncherUpdateChannel fromName(String? value) {
    final normalized = (value ?? '').trim().toLowerCase();
    return switch (normalized) {
      'release' => LauncherUpdateChannel.release,
      'beta' => LauncherUpdateChannel.beta,
      'nightly' => LauncherUpdateChannel.nightly,
      _ => LauncherUpdateChannel.release,
    };
  }
}

class LauncherUpdateSettings {
  const LauncherUpdateSettings({
    this.enabled = true,
    this.checkAutomatically = true,
    this.channel = LauncherUpdateChannel.beta,
    this.archiveUrl = defaultArchiveUrl,
  });

  static const defaultArchiveUrl =
      'https://docs.topiaforge.dev/manual-releases.json';

  final bool enabled;
  final bool checkAutomatically;
  final LauncherUpdateChannel channel;
  final String archiveUrl;

  factory LauncherUpdateSettings.fromJson(Map<String, Object?> json) {
    if (json.containsKey('manualReleasesUrl') ||
        json.containsKey('appArchiveUrl')) {
      throw const FormatException(
        'Retired launcher update URL keys are not supported; use archiveUrl.',
      );
    }
    final configuredUrl = (json['archiveUrl'] as String?)?.trim() ?? '';
    return LauncherUpdateSettings(
      enabled: (json['enabled'] as bool?) ?? true,
      checkAutomatically: (json['checkAutomatically'] as bool?) ?? true,
      channel: json.containsKey('channel')
          ? LauncherUpdateChannel.fromName(json['channel'] as String?)
          : LauncherUpdateChannel.beta,
      archiveUrl: _isTrustedPublicHttpsUrl(configuredUrl)
          ? configuredUrl
          : defaultArchiveUrl,
    );
  }

  Map<String, Object?> toJson() => {
    'enabled': enabled,
    'checkAutomatically': checkAutomatically,
    'channel': channel.name,
    'archiveUrl': archiveUrl,
  };

  LauncherUpdateSettings copyWith({
    bool? enabled,
    bool? checkAutomatically,
    LauncherUpdateChannel? channel,
    String? archiveUrl,
  }) {
    return LauncherUpdateSettings(
      enabled: enabled ?? this.enabled,
      checkAutomatically: checkAutomatically ?? this.checkAutomatically,
      channel: channel ?? this.channel,
      archiveUrl: archiveUrl ?? this.archiveUrl,
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
        url: (json['url'] as String?) ?? '',
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
      releaseUrl: (json['releaseUrl'] as String?) ?? '',
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
      formatVersion == 2 &&
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

enum LauncherUpdatePhase {
  idle,
  checking,
  current,
  available,
  downloading,
  staged,
  applying,
  recovering,
  failed,
}

final class LauncherUpdateArtifact {
  const LauncherUpdateArtifact({
    required this.platform,
    required this.assetName,
    required this.url,
    required this.sha256,
    required this.size,
    required this.entryCount,
    required this.expandedSize,
    required this.installLayout,
  });

  factory LauncherUpdateArtifact.fromJson(
    String platform,
    Map<String, Object?> json,
  ) {
    const fields = {
      'assetName',
      'url',
      'sha256',
      'size',
      'entryCount',
      'expandedSize',
      'installLayout',
    };
    if (json.keys.toSet().difference(fields).isNotEmpty ||
        fields.difference(json.keys.toSet()).isNotEmpty ||
        json['size'] is! int ||
        json['entryCount'] is! int ||
        json['expandedSize'] is! int) {
      throw const FormatException(
        'Launcher update artifact fields are invalid.',
      );
    }
    return LauncherUpdateArtifact(
      platform: platform,
      assetName: json['assetName'] as String? ?? '',
      url: json['url'] as String? ?? '',
      sha256: json['sha256'] as String? ?? '',
      size: (json['size'] as num?)?.toInt() ?? 0,
      entryCount: (json['entryCount'] as num?)?.toInt() ?? 0,
      expandedSize: (json['expandedSize'] as num?)?.toInt() ?? 0,
      installLayout: json['installLayout'] as String? ?? '',
    );
  }

  final String platform;
  final String assetName;
  final String url;
  final String sha256;
  final int size;
  final int entryCount;
  final int expandedSize;
  final String installLayout;

  bool get isValid {
    const names = {
      'windows-x64': 'TopiaForge-windows-x64.zip',
      'linux-x64': 'TopiaForge-linux-x64.zip',
      'macos-universal': 'TopiaForge-macos-universal.zip',
    };
    const layouts = {
      'windows-x64': 'portable-root',
      'linux-x64': 'portable-root',
      'macos-universal': 'app-bundle',
    };
    return names[platform] == assetName &&
        layouts[platform] == installLayout &&
        _isTrustedPublicHttpsUrl(url) &&
        RegExp(r'^[0-9a-f]{64}$').hasMatch(sha256) &&
        size > 0 &&
        size <= 512 * 1024 * 1024 &&
        entryCount > 0 &&
        entryCount <= 20000 &&
        expandedSize > 0 &&
        expandedSize <= 2 * 1024 * 1024 * 1024;
  }
}

final class LauncherUpdateCandidate {
  LauncherUpdateCandidate({
    required this.version,
    required this.tag,
    required this.channel,
    required this.minimumUpdaterVersion,
    required this.releaseUrl,
    required this.signingKeyId,
    required this.payloadSha256,
    required Map<String, LauncherUpdateArtifact> platforms,
  }) : platforms = Map.unmodifiable(platforms) {
    final parsedVersion = SemanticVersion.tryParse(version);
    final minimum = SemanticVersion.tryParse(minimumUpdaterVersion);
    if (parsedVersion == null ||
        minimum == null ||
        tag != 'v$version' ||
        !_isTrustedPublicHttpsUrl(releaseUrl) ||
        platforms.length != 3 ||
        !platforms.values.every((artifact) => artifact.isValid) ||
        !RegExp(r'^ed25519:[0-9a-f]{16}$').hasMatch(signingKeyId) ||
        !RegExp(r'^[0-9a-f]{64}$').hasMatch(payloadSha256)) {
      throw const FormatException('Launcher update candidate is invalid.');
    }
  }

  factory LauncherUpdateCandidate.fromVerifiedJson({
    required Map<String, Object?> json,
    required String signingKeyId,
    required String payloadSha256,
  }) {
    const schema =
        'https://raw.githubusercontent.com/furroxide/TopiaForge/main/'
        'schemas/topiaforge.launcher-update-v1.schema.json';
    const fields = {
      r'$schema',
      'formatVersion',
      'product',
      'version',
      'tag',
      'channel',
      'minimumUpdaterVersion',
      'releaseUrl',
      'platforms',
    };
    final keys = json.keys.toSet();
    if (keys.difference(fields).isNotEmpty ||
        fields.difference(keys).isNotEmpty ||
        json[r'$schema'] != schema ||
        json['formatVersion'] != 1 ||
        json['product'] != 'TopiaForge' ||
        !const {'release', 'beta'}.contains(json['channel']) ||
        json['platforms'] is! Map) {
      throw const FormatException('Launcher update payload is invalid.');
    }
    final rawPlatforms = Map<String, Object?>.from(json['platforms']! as Map);
    if (rawPlatforms.values.any((value) => value is! Map)) {
      throw const FormatException('Launcher update platforms are invalid.');
    }
    return LauncherUpdateCandidate(
      version: json['version'] as String? ?? '',
      tag: json['tag'] as String? ?? '',
      channel: LauncherUpdateChannel.fromName(json['channel'] as String?),
      minimumUpdaterVersion: json['minimumUpdaterVersion'] as String? ?? '',
      releaseUrl: json['releaseUrl'] as String? ?? '',
      signingKeyId: signingKeyId,
      payloadSha256: payloadSha256,
      platforms: {
        for (final entry in rawPlatforms.entries)
          entry.key: LauncherUpdateArtifact.fromJson(
            entry.key,
            Map<String, Object?>.from(entry.value as Map),
          ),
      },
    );
  }

  final String version;
  final String tag;
  final LauncherUpdateChannel channel;
  final String minimumUpdaterVersion;
  final String releaseUrl;
  final String signingKeyId;
  final String payloadSha256;
  final Map<String, LauncherUpdateArtifact> platforms;

  bool isEligibleFor({
    required String currentVersion,
    required LauncherUpdateChannel requestedChannel,
  }) {
    final current = SemanticVersion.tryParse(currentVersion);
    final target = SemanticVersion.tryParse(version);
    final minimum = SemanticVersion.tryParse(minimumUpdaterVersion);
    if (current == null ||
        target == null ||
        minimum == null ||
        target.compareTo(current) <= 0 ||
        current.compareTo(minimum) < 0) {
      return false;
    }
    return switch (requestedChannel) {
      LauncherUpdateChannel.release => channel == LauncherUpdateChannel.release,
      LauncherUpdateChannel.beta =>
        channel == LauncherUpdateChannel.release ||
            channel == LauncherUpdateChannel.beta,
      LauncherUpdateChannel.nightly => true,
    };
  }
}

final class LauncherUpdateStatus {
  const LauncherUpdateStatus({
    this.phase = LauncherUpdatePhase.idle,
    this.candidate,
    this.progress = 0,
    this.message = '',
    this.stagedPlanPath = '',
  });

  final LauncherUpdatePhase phase;
  final LauncherUpdateCandidate? candidate;
  final double progress;
  final String message;
  final String stagedPlanPath;

  LauncherUpdateStatus copyWith({
    LauncherUpdatePhase? phase,
    LauncherUpdateCandidate? candidate,
    double? progress,
    String? message,
    String? stagedPlanPath,
    bool clearCandidate = false,
  }) => LauncherUpdateStatus(
    phase: phase ?? this.phase,
    candidate: clearCandidate ? null : candidate ?? this.candidate,
    progress: progress ?? this.progress,
    message: message ?? this.message,
    stagedPlanPath: stagedPlanPath ?? this.stagedPlanPath,
  );
}
