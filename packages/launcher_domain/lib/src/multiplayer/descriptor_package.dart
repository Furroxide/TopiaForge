part of '../multiplayer.dart';

/// What a joining player must do about one package the host runs, as the
/// host's descriptor claims it. Join planning never trusts the claim: it reads
/// the package's own manifest, resolved by its SHA-256.
enum DescriptorPackageRole {
  /// A `session` mod with `required` presence: the exact package must be
  /// installed.
  sessionRequired('session-required'),

  /// A `session` mod with `optional` presence: used when installed at the
  /// exact package.
  sessionOptional('session-optional'),

  /// A mod with no `multiplayer` block, shared implicitly: required at the
  /// exact package.
  implicit('implicit');

  const DescriptorPackageRole(this.wireName);

  final String wireName;

  static DescriptorPackageRole? tryParse(String value) {
    for (final role in values) {
      if (role.wireName == value) return role;
    }
    return null;
  }
}

/// One package the host runs, pinned to its exact archive.
final class DescriptorPackage {
  DescriptorPackage({
    required this.id,
    required this.version,
    required this.sha256,
    required this.role,
  }) {
    if (!isModId(id)) {
      throw ArgumentError.value(id, 'id', 'A package id is a manifest mod id.');
    }
    if (!_isSemanticVersion(version)) {
      throw ArgumentError.value(
        version,
        'version',
        'A package version is a semantic version.',
      );
    }
    if (!WireText.isHex(sha256, 32)) {
      throw ArgumentError.value(
        sha256,
        'sha256',
        'A package digest is 64 lowercase hexadecimal characters.',
      );
    }
  }

  final String id;
  final String version;

  /// The lowercase hexadecimal SHA-256 of the package archive.
  final String sha256;
  final DescriptorPackageRole role;

  /// `id@version@sha256`, the exact identity a join installs.
  String get pin => '$id@$version@$sha256';

  Map<String, Object?> toJson() => {
    'id': id,
    'version': version,
    'sha256': sha256,
    'role': role.wireName,
  };

  /// Reads one package entry, refusing unknown or missing members.
  factory DescriptorPackage.fromJson(Object? value) {
    if (value is! Map<String, Object?> ||
        !_hasExactly(value, const {'id', 'version', 'sha256', 'role'})) {
      throw const FormatException(
        'A package entry has exactly an id, version, sha256 and role.',
      );
    }
    final role = DescriptorPackageRole.tryParse(_requireString(value, 'role'));
    if (role == null) {
      throw const FormatException(
        'A package role is session-required, session-optional or implicit.',
      );
    }
    try {
      return DescriptorPackage(
        id: _requireString(value, 'id'),
        version: _requireString(value, 'version'),
        sha256: _requireString(value, 'sha256'),
        role: role,
      );
    } on ArgumentError catch (error) {
      throw FormatException('A package entry is invalid: ${error.message}');
    }
  }

  @override
  bool operator ==(Object other) =>
      other is DescriptorPackage &&
      other.id == id &&
      other.version == version &&
      other.sha256 == sha256 &&
      other.role == role;

  @override
  int get hashCode => Object.hash(id, version, sha256, role);

  @override
  String toString() => '$pin (${role.wireName})';

  static final _modIdPattern = RegExp(r'^[A-Za-z0-9][A-Za-z0-9_.-]{1,63}$');

  /// Whether [id] has the manifest mod-id shape.
  static bool isModId(String? id) => id != null && _modIdPattern.hasMatch(id);
}

final _descriptorSemanticVersionPattern = RegExp(
  r'^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)'
  r'(?:-(?:0|[1-9][0-9]*|[0-9A-Za-z-]*[A-Za-z-][0-9A-Za-z-]*)'
  r'(?:\.(?:0|[1-9][0-9]*|[0-9A-Za-z-]*[A-Za-z-][0-9A-Za-z-]*))*)?'
  r'(?:\+[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?$',
);

final _gameBuildPattern = RegExp(r'^[0-9A-Za-z._-]{1,16}$');

/// The longest semantic version a descriptor or session info carries.
const _maximumVersionLength = 64;

// Dart's `$` matches only at the very end of the input. .NET's also matches
// before a final line feed, so these patterns are stricter than a literal
// port of the game's would be: "1.0.0\n" is not a version here.
bool _isSemanticVersion(String? value) =>
    value != null &&
    value.length <= _maximumVersionLength &&
    _descriptorSemanticVersionPattern.hasMatch(value);

bool _isGameBuild(String? value) =>
    value != null && _gameBuildPattern.hasMatch(value);

bool _hasExactly(Map<String, Object?> value, Set<String> keys) =>
    value.length == keys.length && keys.every(value.containsKey);

String _requireString(Map<String, Object?> value, String key) {
  final item = value[key];
  if (item is String) return item;
  throw FormatException("'$key' must be a string.");
}

String? _optionalString(Map<String, Object?> value, String key) {
  if (!value.containsKey(key)) throw FormatException("'$key' is missing.");
  final item = value[key];
  if (item == null || item is String) return item as String?;
  throw FormatException("'$key' must be a string or null.");
}

int _requireInteger(Map<String, Object?> value, String key) {
  final item = value[key];
  if (item is int) return item;
  throw FormatException("'$key' must be an integer.");
}

bool _requireBoolean(Map<String, Object?> value, String key) {
  final item = value[key];
  if (item is bool) return item;
  throw FormatException("'$key' must be true or false.");
}
