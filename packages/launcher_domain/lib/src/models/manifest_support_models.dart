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
    return ModConflict(
      id: (json['id'] as String?) ?? '',
      versionRange: VersionRange.parse(
        (json['versionRange'] as String?) ?? (json['version'] as String?),
      ),
      reason: (json['reason'] as String?) ?? '',
    );
  }

  Map<String, Object?> toJson() => {
    'id': id,
    'versionRange': versionRange.toString(),
    if (reason.isNotEmpty) 'reason': reason,
  };
}
