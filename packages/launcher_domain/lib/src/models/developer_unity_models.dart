part of '../models.dart';

/// Unity companion project settings for the AssetBundle authoring workflow
/// (`topiaforge world build`). The browser-based Robotopia Creator covers
/// scene authoring; this covers the Unity-authored bundle path that it cannot.
class UnityCompanionSettings {
  const UnityCompanionSettings({
    this.enabled = false,
    this.projectPath = '',
    this.unityVersion = '',
    this.assetBundleOutputPath = '',
  });

  final bool enabled;
  final String projectPath;
  final String unityVersion;
  final String assetBundleOutputPath;

  factory UnityCompanionSettings.fromJson(Map<String, Object?> json) {
    return UnityCompanionSettings(
      enabled: (json['enabled'] as bool?) ?? false,
      projectPath: (json['projectPath'] as String?) ?? '',
      unityVersion: (json['unityVersion'] as String?) ?? '',
      assetBundleOutputPath: (json['assetBundleOutputPath'] as String?) ?? '',
    );
  }

  Map<String, Object?> toJson() => {
    'enabled': enabled,
    if (projectPath.isNotEmpty) 'projectPath': projectPath,
    if (unityVersion.isNotEmpty) 'unityVersion': unityVersion,
    if (assetBundleOutputPath.isNotEmpty)
      'assetBundleOutputPath': assetBundleOutputPath,
  };
}
