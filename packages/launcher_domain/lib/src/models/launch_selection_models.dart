part of '../models.dart';

enum LaunchSelectionKind { mainMenu, target, unresolvedLegacy }

/// Durable intent. Legacy bytes are JSON values, never inferred declarations.
final class LaunchSelection {
  const LaunchSelection.mainMenu()
    : kind = LaunchSelectionKind.mainMenu,
      request = null,
      legacy = null;

  LaunchSelection.target(LaunchRequest request)
    : kind = LaunchSelectionKind.target,
      request = LaunchRequest.fromJson(request.toJson()),
      legacy = null;

  LaunchSelection.unresolvedLegacy(Map<String, Object?> legacy)
    : kind = LaunchSelectionKind.unresolvedLegacy,
      request = null,
      legacy = _freezeSelectionJson(legacy) as Map<String, Object?>;

  static const schemaVersion = 1;
  final LaunchSelectionKind kind;
  final LaunchRequest? request;
  final Map<String, Object?>? legacy;

  factory LaunchSelection.fromJson(Object? value) {
    if (value is! Map<String, Object?> ||
        value['schemaVersion'] is! int ||
        value['schemaVersion'] != 1) {
      throw const FormatException('Unsupported durable launch selection.');
    }
    final kind = value['kind'];
    final keys = switch (kind) {
      'main-menu' => {'schemaVersion', 'kind'},
      'target' => {'schemaVersion', 'kind', 'request'},
      'unresolved-legacy' => {'schemaVersion', 'kind', 'legacy'},
      _ => throw const FormatException('Unknown durable launch selection.'),
    };
    if (value.length != keys.length || !keys.containsAll(value.keys)) {
      throw const FormatException('Invalid durable launch selection fields.');
    }
    return switch (kind) {
      'main-menu' => const LaunchSelection.mainMenu(),
      'target' => LaunchSelection.target(
        LaunchRequest.fromJson(value['request']),
      ),
      _ =>
        value['legacy'] is Map<String, Object?>
            ? LaunchSelection.unresolvedLegacy(
                value['legacy'] as Map<String, Object?>,
              )
            : throw const FormatException(
                'Legacy selection must be an object.',
              ),
    };
  }

  Map<String, Object?> toJson() => {
    'schemaVersion': schemaVersion,
    'kind': switch (kind) {
      LaunchSelectionKind.mainMenu => 'main-menu',
      LaunchSelectionKind.target => 'target',
      LaunchSelectionKind.unresolvedLegacy => 'unresolved-legacy',
    },
    if (request != null) 'request': request!.toJson(),
    if (legacy != null) 'legacy': legacy,
  };

  @override
  bool operator ==(Object other) =>
      other is LaunchSelection &&
      _selectionCanonical(toJson()) == _selectionCanonical(other.toJson());
  @override
  int get hashCode => _selectionCanonical(toJson()).hashCode;
}

Object? _freezeSelectionJson(Object? value, [int depth = 0]) {
  if (depth > 64) throw const FormatException('Excessive selection nesting.');
  if (value == null || value is String || value is bool || value is int) {
    return value;
  }
  if (value is double && value.isFinite) return value;
  if (value is List) {
    return List<Object?>.unmodifiable(
      value.map((item) => _freezeSelectionJson(item, depth + 1)),
    );
  }
  if (value is Map<String, Object?>) {
    return Map<String, Object?>.unmodifiable(
      value.map(
        (key, item) => MapEntry(key, _freezeSelectionJson(item, depth + 1)),
      ),
    );
  }
  throw const FormatException('Selection contains a non-JSON value.');
}

String _selectionCanonical(Object? value) {
  if (value is Map<String, Object?>) {
    final keys = value.keys.toList()..sort();
    return '{${keys.map((key) => '${jsonEncode(key)}:${_selectionCanonical(value[key])}').join(',')}}';
  }
  if (value is List) return '[${value.map(_selectionCanonical).join(',')}]';
  return jsonEncode(value);
}
