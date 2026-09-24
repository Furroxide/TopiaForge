part of '../launch_resolution.dart';

Map<String, Object?> _object(
  Object? value,
  Set<String> allowed,
  Set<String> required,
) {
  if (value is! Map<String, Object?> ||
      value.keys.any((key) => !allowed.contains(key)) ||
      required.any((key) => !value.containsKey(key))) {
    throw const FormatException('Invalid launch contract object.');
  }
  return value;
}

String _rawString(Object? value) {
  if (value is! String) throw const FormatException('Expected JSON string.');
  return value;
}

String _text(Object? value, {int max = 1024, bool empty = false}) {
  if (value is! String ||
      (!empty && value.isEmpty) ||
      value.runes.length > max) {
    throw const FormatException('Invalid launch contract text.');
  }
  return value;
}

String _declaration(Object? value) {
  final id = _text(value, max: 96);
  if (!ModContributions.isValidDeclarationId(id)) {
    throw const FormatException('Invalid declaration id.');
  }
  return id;
}

String _packageId(Object? value) {
  final id = _text(value, max: 64);
  if (!ModManifest.isValidId(id)) {
    throw const FormatException('Invalid package id.');
  }
  return id;
}

String _version(Object? value) {
  if (value is! String) throw const FormatException('Invalid package version.');
  final version = value;
  if (SemanticVersion.tryParse(version) == null) {
    throw const FormatException('Invalid package version.');
  }
  return version;
}

String _token(Object? value) {
  final token = _text(value, max: 128);
  if (!RegExp(r'^[A-Za-z0-9][A-Za-z0-9_.-]{0,127}$').hasMatch(token)) {
    throw const FormatException('Invalid request/profile/session identifier.');
  }
  return token;
}

int _integer(Object? value) {
  if (value is! num ||
      !value.isFinite ||
      value < 0 ||
      value > 2147483647 ||
      value != value.truncateToDouble()) {
    throw const FormatException('Invalid nonnegative integer.');
  }
  return value.toInt();
}

bool _boolean(Object? value) {
  if (value is! bool) throw const FormatException('Invalid boolean.');
  return value;
}

List<T> _list<T>(Object? value, T Function(Object?) read, {int max = 4096}) {
  if (value is! List || value.length > max) {
    throw const FormatException('Invalid bounded collection.');
  }
  return List<T>.unmodifiable(value.map(read));
}

String _choice(Object? value, Iterable<String> choices) {
  final text = _text(value);
  if (!choices.contains(text)) {
    throw const FormatException('Unknown contract value.');
  }
  return text;
}

String _digest(Object? value) {
  final digest = _text(value, max: 16);
  if (!RegExp(r'^[0-9a-f]{16}$').hasMatch(digest)) {
    throw const FormatException('Invalid package digest.');
  }
  return digest;
}

List<T> _boundedCopy<T>(Iterable<T> source) {
  final result = source.take(4097).toList();
  if (result.length > 4096) {
    throw const FormatException('Excessive collection.');
  }
  return List.unmodifiable(result);
}

List<PackageIdentity> _identities(Iterable<PackageIdentity> source) {
  final result =
      source
          .map((item) => PackageIdentity(id: item.id, version: item.version))
          .toList()
        ..sort((a, b) {
          final byId = a.id.compareTo(b.id);
          return byId != 0 ? byId : a.version.compareTo(b.version);
        });
  final seen = <String>{};
  if (result.length > 4096 ||
      result.any((item) => !seen.add(item.id.toLowerCase()))) {
    throw const FormatException('Duplicate or excessive package identities.');
  }
  return List.unmodifiable(result);
}

bool _samePackages(
  Iterable<PackageIdentity> left,
  Iterable<PackageIdentity> right,
) {
  final a = left.map((item) => '${item.id}@${item.version}').toList()..sort();
  final b = right.map((item) => '${item.id}@${item.version}').toList()..sort();
  return a.length == b.length &&
      List.generate(a.length, (i) => a[i] == b[i]).every((v) => v);
}

List<String> _ids(Object? value, {bool packages = false}) {
  final result = _list(value, packages ? _packageId : _declaration).toList()
    ..sort();
  final seen = <String>{};
  if (result.any((id) => !seen.add(id.toLowerCase()))) {
    throw const FormatException('Duplicate identifiers.');
  }
  return List.unmodifiable(result);
}
