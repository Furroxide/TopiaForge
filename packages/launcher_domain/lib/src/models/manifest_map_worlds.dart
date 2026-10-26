part of '../models.dart';

void _mapContentShape(
  Map<Object?, Object?> fields,
  String path,
  _ContributionShape shape,
) {
  shape.conditional(fields, path, const {}, const {
    'bundle',
    'prefab',
    'sceneName',
    'implementation',
  });
  if (fields.containsKey('map') == fields.containsKey('file')) {
    shape.error('$path requires exactly one of map or file.');
  }
  bool identifier(String value) => !RegExp(r'[^A-Za-z0-9_-]').hasMatch(value);
  shape.text(
    fields,
    'map',
    path,
    minimum: 1,
    maximum: 64,
    accepts: identifier,
    grammar: 'an ASCII map identifier, not a URL or path',
  );
  shape.text(
    fields,
    'scene',
    path,
    minimum: 1,
    maximum: 128,
    accepts: identifier,
    grammar: 'an ASCII scene identifier',
  );
  shape.path(fields, 'file', path);
  shape.text(
    fields,
    'file',
    path,
    minimum: 1,
    maximum: 1024,
    accepts: (value) {
      final lower = value.toLowerCase();
      return lower.endsWith('.roboworld') ||
          lower.endsWith('.json') ||
          lower.endsWith('.json.gz');
    },
    grammar: 'a .roboworld, .json or .json.gz file',
  );
}

void _validateMapWorldContent(
  ModWorldContent content,
  String path,
  ModManifest manifest,
  void Function(String) error,
) {
  if (!manifest.dependencies.any(
    (dependency) =>
        dependency.id.toLowerCase() == 'io.github.furroxide.topiaforge.worlds',
  )) {
    error('$path requires the Worlds package in dependencies.');
  }
}
