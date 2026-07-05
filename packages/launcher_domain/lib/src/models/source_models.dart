part of '../models.dart';

class PackageSource {
  const PackageSource({
    required this.id,
    required this.name,
    required this.url,
    this.enabled = true,
    this.builtIn = false,
  });

  final String id;
  final String name;
  final String url;
  final bool enabled;
  final bool builtIn;

  factory PackageSource.fromJson(Map<String, Object?> json) {
    return PackageSource(
      id: (json['id'] as String?) ?? '',
      name: (json['name'] as String?) ?? '',
      url: (json['url'] as String?) ?? '',
      enabled: (json['enabled'] as bool?) ?? true,
      builtIn: (json['builtIn'] as bool?) ?? false,
    );
  }

  Map<String, Object?> toJson() => {
    'id': id,
    'name': name,
    'url': url,
    'enabled': enabled,
    if (builtIn) 'builtIn': true,
  };

  PackageSource copyWith({String? name, String? url, bool? enabled}) {
    return PackageSource(
      id: id,
      name: name ?? this.name,
      url: url ?? this.url,
      enabled: enabled ?? this.enabled,
      builtIn: builtIn,
    );
  }
}

/// Health of one enabled package source after a catalog load, so the UI can
/// say "this source is down" instead of silently showing fewer mods.
class PackageSourceStatus {
  const PackageSourceStatus({
    required this.sourceId,
    required this.sourceName,
    required this.ok,
    this.message = '',
    this.modCount = 0,
    this.remote = false,
  });

  final String sourceId;
  final String sourceName;
  final bool ok;
  final String message;
  final int modCount;

  /// True for https sources — the ones that fail when the player is offline.
  final bool remote;
}

class PackageInstallAction {
  const PackageInstallAction({
    required this.modId,
    required this.name,
    required this.version,
    required this.packageUrl,
    required this.packageSha256,
    this.sourceId = '',
    this.sourceName = '',
    this.root = false,
  });

  final String modId;
  final String name;
  final String version;
  final String packageUrl;
  final String packageSha256;
  final String sourceId;
  final String sourceName;
  final bool root;

  bool get isRemote => packageUrl.startsWith('https://');
}
