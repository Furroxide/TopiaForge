part of '../models.dart';

/// Decodes a schemaVersion 6 manifest.
///
/// The only version-specific field is `contributions`; everything else comes
/// from [_readCommonManifestFields], so a later schema adds a reader beside this
/// one rather than widening it.
ModManifest _readV6Manifest(Map<String, Object?> json) {
  return _readCommonManifestFields(
    json,
    contributions: json.containsKey('contributions')
        ? ModContributions.fromJson(json['contributions'])
        : null,
  );
}
