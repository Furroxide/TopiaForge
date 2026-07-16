part of '../models.dart';

class ModAuthor {
  const ModAuthor({this.name = '', this.email = '', this.url = ''});

  final String name;
  final String email;
  final String url;

  bool get isEmpty =>
      name.trim().isEmpty && email.trim().isEmpty && url.trim().isEmpty;

  factory ModAuthor.fromJson(Object? value) {
    if (value is String) {
      return ModAuthor(name: value);
    }
    final json = _objectMap(value);
    return ModAuthor(
      name: (json['name'] as String?) ?? '',
      email: (json['email'] as String?) ?? '',
      url: (json['url'] as String?) ?? '',
    );
  }

  Map<String, Object?> toJson() => {
    'name': name,
    if (email.isNotEmpty) 'email': email,
    if (url.isNotEmpty) 'url': url,
  };
}

class ModBuildMetadata {
  const ModBuildMetadata({
    this.sdkVersion = '',
    this.loaderVersion = '',
    this.gameVersion = '',
    this.toolVersion = '',
  });

  final String sdkVersion;
  final String loaderVersion;
  final String gameVersion;
  final String toolVersion;

  factory ModBuildMetadata.fromJson(Object? value) {
    if (value == null) return const ModBuildMetadata();
    final json = _objectMap(value);
    return ModBuildMetadata(
      sdkVersion: (json['sdkVersion'] as String?) ?? '',
      loaderVersion: (json['loaderVersion'] as String?) ?? '',
      gameVersion: (json['gameVersion'] as String?) ?? '',
      toolVersion: (json['toolVersion'] as String?) ?? '',
    );
  }

  bool get isEmpty =>
      sdkVersion.isEmpty &&
      loaderVersion.isEmpty &&
      gameVersion.isEmpty &&
      toolVersion.isEmpty;

  Map<String, Object?> toJson() => {
    if (sdkVersion.isNotEmpty) 'sdkVersion': sdkVersion,
    if (loaderVersion.isNotEmpty) 'loaderVersion': loaderVersion,
    if (gameVersion.isNotEmpty) 'gameVersion': gameVersion,
    if (toolVersion.isNotEmpty) 'toolVersion': toolVersion,
  };
}

class ModConflict {
  const ModConflict({
    required this.id,
    this.versionRange = const VersionRange.any(),
    this.reason = '',
  });

  final String id;
  final VersionRange versionRange;
  final String reason;

  factory ModConflict.fromJson(Map<String, Object?> json) {
    if (json.containsKey('version')) {
      throw const FormatException(
        'Conflict version aliases are not supported; use versionRange.',
      );
    }
    return ModConflict(
      id: (json['id'] as String?) ?? '',
      versionRange: VersionRange.parse(json['versionRange'] as String?),
      reason: (json['reason'] as String?) ?? '',
    );
  }

  Map<String, Object?> toJson() => {
    'id': id,
    'versionRange': versionRange.toString(),
    if (reason.isNotEmpty) 'reason': reason,
  };
}
