part of '../models.dart';

/// A package-local theme definition declared in the UI catalogue.
final class ModUiThemeDeclaration {
  const ModUiThemeDeclaration._(this.id, this.definition, this.sha256);

  final String id;
  final String definition;

  /// SHA-256 of the exact packaged definition bytes, when known.
  final String? sha256;

  Map<String, Object?> toJson() => {
    'id': id,
    'definition': definition,
    if (sha256 != null) 'sha256': sha256,
  };
}

/// Data-only theme declarations for a mod package.
final class ModUiCatalogue {
  ModUiCatalogue._(this.defaultThemeId, List<ModUiThemeDeclaration> themes)
    : themes = List.unmodifiable(themes);

  final String? defaultThemeId;
  final List<ModUiThemeDeclaration> themes;

  Map<String, Object?> toJson() => {
    'schema': 1,
    if (defaultThemeId != null) 'default': defaultThemeId,
    'themes': themes.map((theme) => theme.toJson()).toList(growable: false),
  };
}

final _uiThemeId = RegExp(r'^[a-z0-9][a-z0-9._-]{0,63}$');
final _uiThemeSha256 = RegExp(r'^[a-fA-F0-9]{64}$');

bool _validUiThemeId(Object? id) =>
    id is String && _uiThemeId.hasMatch(id) && !id.startsWith('topiaforge.');

ModUiCatalogue? _readModUiCatalogue(Object? raw, List<String> issues) {
  if (raw is! Map) {
    issues.add('ui must be an object.');
    return null;
  }
  if (raw.keys.any(
    (key) => !const {'schema', 'default', 'themes'}.contains(key),
  )) {
    issues.add('ui contains an unknown field.');
  }
  if (raw['schema'] is! int || raw['schema'] != 1) {
    issues.add('ui.schema must be 1.');
  }

  final defaultId = raw['default'];
  if (raw.containsKey('default') && !_validUiThemeId(defaultId)) {
    issues.add('ui.default must be a valid theme identifier.');
  }

  final entries = raw['themes'];
  if (entries is! List || entries.length > 8) {
    issues.add('ui.themes must be an array with at most eight entries.');
    return null;
  }

  final ids = <String>{};
  final themes = <ModUiThemeDeclaration>[];
  for (var index = 0; index < entries.length; index++) {
    final path = 'ui.themes[$index]';
    final entry = entries[index];
    if (entry is! Map) {
      issues.add('$path must be an object.');
      continue;
    }
    if (entry.keys.any(
      (key) => !const {'id', 'definition', 'sha256'}.contains(key),
    )) {
      issues.add('$path contains an unknown field.');
    }
    final id = entry['id'];
    if (id is! String || !_validUiThemeId(id)) {
      issues.add('$path.id must be a valid theme identifier.');
      continue;
    }
    if (!ids.add(id)) {
      issues.add('$path.id must be unique in the catalogue.');
    }
    final definition = entry['definition'];
    if (definition is! String ||
        utf8.encode(definition).length > 256 ||
        _portableManifestPathCollisionKey(definition) == null) {
      issues.add(
        '$path.definition must be a portable relative path of at most 256 UTF-8 bytes.',
      );
      continue;
    }
    final digest = entry['sha256'];
    if (entry.containsKey('sha256') &&
        (digest is! String || !_uiThemeSha256.hasMatch(digest))) {
      issues.add('$path.sha256 must be a 64-character SHA-256 digest.');
      continue;
    }
    themes.add(ModUiThemeDeclaration._(id, definition, digest as String?));
  }
  if (defaultId is String && !ids.contains(defaultId)) {
    issues.add('ui.default must name a theme declared in ui.themes.');
  }
  return ModUiCatalogue._(defaultId is String ? defaultId : null, themes);
}
