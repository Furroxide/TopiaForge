part of '../models.dart';

/// Decodes the current contribution contract.
ModManifest _readV6Manifest(Map<String, Object?> json) {
  return _readCommonManifestFields(
    json,
    contributions: json.containsKey('contributions')
        ? ModContributions.fromJson(json['contributions'])
        : null,
  );
}
