part of '../models.dart';

/// Decodes a schemaVersion 6 manifest.
///
/// V6 differs from V5 in exactly two places: `worldGamemodes` is gone, and
/// `contributions` declares worlds, gamemodes and launch targets as separate
/// things with an implementation owner. Everything else is carried over
/// unchanged, so the two readers share every other field mapping through
/// [_readCommonManifestFields] rather than restating it -- a field read
/// differently by one version and not the other is precisely the drift these
/// separate readers exist to make visible.
ModManifest _readV6Manifest(Map<String, Object?> json) {
  return _readCommonManifestFields(
    json,
    worldGamemodes: const [],
    contributions: json.containsKey('contributions')
        ? ModContributions.fromJson(json['contributions'])
        : null,
  );
}
