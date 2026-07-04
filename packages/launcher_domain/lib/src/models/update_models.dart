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
    this.enabled = true,
    this.checkAutomatically = true,
    this.channel = LauncherUpdateChannel.release,
    this.appArchiveUrl = defaultAppArchiveUrl,
  });

  static const defaultAppArchiveUrl =
      'https://furroxide.github.io/quantum-works/app-archive.json';

  final bool enabled;
  final bool checkAutomatically;
  final LauncherUpdateChannel channel;
  final String appArchiveUrl;

  factory LauncherUpdateSettings.fromJson(Map<String, Object?> json) {
    return LauncherUpdateSettings(
      enabled: (json['enabled'] as bool?) ?? true,
      checkAutomatically: (json['checkAutomatically'] as bool?) ?? true,
      channel: LauncherUpdateChannel.fromName(json['channel'] as String?),
      appArchiveUrl: (json['appArchiveUrl'] as String?) ?? defaultAppArchiveUrl,
    );
  }

  Map<String, Object?> toJson() => {
    'enabled': enabled,
    'checkAutomatically': checkAutomatically,
    'channel': channel.name,
    'appArchiveUrl': appArchiveUrl,
  };

  LauncherUpdateSettings copyWith({
    bool? enabled,
    bool? checkAutomatically,
    LauncherUpdateChannel? channel,
    String? appArchiveUrl,
  }) {
    return LauncherUpdateSettings(
      enabled: enabled ?? this.enabled,
      checkAutomatically: checkAutomatically ?? this.checkAutomatically,
      channel: channel ?? this.channel,
      appArchiveUrl: appArchiveUrl ?? this.appArchiveUrl,
    );
  }
}
