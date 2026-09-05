part of '../models.dart';

/// Shared primitive checks for the V6 contribution shape. JSON null is never
/// an omitted property; collection elements are checked before typed decoding.
class _ContributionShape {
  const _ContributionShape(this.issues);

  final List<String> issues;

  void error(String message) => issues.add(message);

  Map<Object?, Object?>? object(
    Object? value,
    String path,
    Set<String> allowed,
    Set<String> required, {
    bool nonEmpty = false,
  }) {
    if (value is! Map) {
      error('$path must be an object.');
      return null;
    }
    if (nonEmpty && value.isEmpty) {
      error('$path must contain at least one property.');
    }
    for (final key in value.keys) {
      if (!allowed.contains(key)) error('$path contains unknown field $key.');
    }
    conditional(value, path, required, const {});
    return value;
  }

  Iterable<({String path, Object? value})> objects(
    Map<Object?, Object?> parent,
    String key,
    String path,
    int maximum,
  ) sync* {
    if (!parent.containsKey(key)) return;
    final value = parent[key];
    final arrayPath = '$path.$key';
    if (value is! List) {
      error('$arrayPath must be an array.');
      return;
    }
    if (value.isEmpty || value.length > maximum) {
      error('$arrayPath must contain between 1 and $maximum entries.');
    }
    for (var index = 0; index < value.length; index++) {
      yield (path: '$arrayPath[$index]', value: value[index]);
    }
  }

  void conditional(
    Map<Object?, Object?> fields,
    String path,
    Set<String> required,
    Set<String> forbidden,
  ) {
    for (final field in required) {
      if (!fields.containsKey(field)) {
        error('$path is missing required field $field.');
      }
    }
    for (final field in forbidden) {
      if (fields.containsKey(field)) {
        error('$path cannot contain field $field for this selection.');
      }
    }
  }

  void text(
    Map<Object?, Object?> fields,
    String key,
    String path, {
    int minimum = 0,
    int maximum = 1024,
    Iterable<String>? choices,
    bool Function(String)? accepts,
    String grammar = 'the required grammar',
  }) {
    if (!fields.containsKey(key)) return;
    final value = fields[key];
    final fieldPath = '$path.$key';
    if (value is! String) {
      error('$fieldPath must be a string.');
      return;
    }
    final length = value.runes.length;
    if (length < minimum || length > maximum) {
      error(
        '$fieldPath must contain between $minimum and $maximum Unicode characters.',
      );
    }
    if (choices != null && !choices.contains(value)) {
      error('$fieldPath must be one of ${choices.join(', ')}.');
    }
    if (accepts != null && !accepts(value)) {
      error('$fieldPath must match $grammar.');
    }
  }

  void id(Map<Object?, Object?> fields, String key, String path) => text(
    fields,
    key,
    path,
    minimum: _minDeclarationIdLength,
    maximum: _maxDeclarationIdLength,
    accepts: _isDeclarationIdShape,
    grammar: 'the ASCII declaration identifier grammar',
  );

  void boolean(Map<Object?, Object?> fields, String key, String path) {
    if (fields.containsKey(key) && fields[key] is! bool) {
      error('$path.$key must be a boolean.');
    }
  }

  void integer(
    Map<Object?, Object?> fields,
    String key,
    String path,
    int minimum,
    int maximum,
  ) {
    if (!fields.containsKey(key)) return;
    final value = fields[key];
    if (value is! num ||
        !value.isFinite ||
        value != value.truncateToDouble() ||
        value < minimum ||
        value > maximum) {
      error('$path.$key must be an integer between $minimum and $maximum.');
    }
  }

  void strings(
    Map<Object?, Object?> fields,
    String key,
    String path, {
    int minimum = 0,
    required int maximum,
    Iterable<String>? choices,
    bool declarationIds = false,
  }) {
    if (!fields.containsKey(key)) return;
    final value = fields[key];
    final fieldPath = '$path.$key';
    if (value is! List) {
      error('$fieldPath must be an array.');
      return;
    }
    if (value.length < minimum || value.length > maximum) {
      error('$fieldPath must contain between $minimum and $maximum entries.');
    }
    final seen = <String>{};
    for (var index = 0; index < value.length; index++) {
      final item = value[index];
      if (item is! String) {
        error('$fieldPath[$index] must be a string.');
        continue;
      }
      if (!seen.add(item)) error('$fieldPath contains duplicate value $item.');
      if (choices != null && !choices.contains(item)) {
        error('$fieldPath[$index] must be one of ${choices.join(', ')}.');
      }
      if (declarationIds && !_isDeclarationIdShape(item)) {
        error(
          '$fieldPath[$index] must match the ASCII declaration identifier grammar.',
        );
      }
    }
  }

  void path(
    Map<Object?, Object?> fields,
    String key,
    String path, {
    bool dll = false,
  }) {
    text(
      fields,
      key,
      path,
      minimum: 1,
      maximum: 1024,
      accepts: (value) =>
          _contributionPortablePathPattern.hasMatch(value) &&
          (!dll || value.toLowerCase().endsWith('.dll')),
      grammar: dll
          ? 'a portable package-relative DLL path'
          : 'a portable package-relative path',
    );
  }
}

// Match the schema's lexical rule here; canonical Unicode and segment collision
// safety remain semantic checks in the portable package-path validator.
final _contributionPortablePathPattern = RegExp(
  r'^(?!/)(?!.*\\)(?!.*:)(?!.*[\u0000-\u001F])'
  r'(?!.*(?:^|/)(?:\.{1,2}|[Cc][Oo][Nn]|[Pp][Rr][Nn]|[Aa][Uu][Xx]|[Nn][Uu][Ll]|'
  r'[Cc][Oo][Mm][1-9]|[Ll][Pp][Tt][1-9])(?:\.|/|$))'
  r'(?!.*[. ](?:/|$))(?!.*//).+$',
);
