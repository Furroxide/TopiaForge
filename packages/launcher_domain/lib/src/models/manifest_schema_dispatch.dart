part of '../models.dart';

enum _ManifestSchemaContract { v6 }

_ManifestSchemaContract _dispatchManifestSchema(Map<String, Object?> json) {
  if (!json.containsKey('schemaVersion')) {
    throw const FormatException(
      "Manifest is missing required field 'schemaVersion'.",
    );
  }

  final schemaVersion = json['schemaVersion'];
  if (schemaVersion is! int) {
    throw const FormatException(
      "Manifest field 'schemaVersion' must be an integer.",
    );
  }
  if (schemaVersion == 4) {
    throw const FormatException(
      'Manifest schemaVersion 4 was retired before TopiaForge 1.0. Run '
      '`topiaforge migrate-manifest` to migrate to schemaVersion 5; omit '
      'multiplayer for a standalone-only mod.',
    );
  }
  if (schemaVersion == ModManifest.manifestV5SchemaVersion) {
    throw const FormatException(
      'Manifest schemaVersion 5 was retired before TopiaForge 1.0. Its '
      'worldGamemodes list named gamemodes without an implementation owner, a '
      'world, or a launch identity. Run `topiaforge migrate-manifest --project '
      '<path>` to move to schemaVersion 6.',
    );
  }
  switch (schemaVersion) {
    case ModManifest.manifestV6SchemaVersion:
      return _ManifestSchemaContract.v6;
    default:
      throw FormatException(
        'Unsupported manifest schemaVersion $schemaVersion; schemaVersion 6 '
        'is required.',
      );
  }
}
