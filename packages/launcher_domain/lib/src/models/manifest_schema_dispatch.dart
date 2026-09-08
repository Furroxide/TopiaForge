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
  if (schemaVersion == 4 || schemaVersion == 5) {
    throw FormatException(
      'Manifest schemaVersion $schemaVersion was retired before TopiaForge 1.0. Run '
      '`topiaforge migrate-manifest` to migrate to schemaVersion 6; omit '
      'multiplayer for a standalone-only mod.',
    );
  }
  switch (schemaVersion) {
    case ModManifest.manifestV6SchemaVersion:
      return _ManifestSchemaContract.v6;
    default:
      throw FormatException(
        'Unsupported manifest schemaVersion $schemaVersion; schemaVersion 6 is required.',
      );
  }
}
