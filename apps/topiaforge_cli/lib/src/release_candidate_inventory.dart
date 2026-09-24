import 'release_strict_json.dart';

/// Qualification has a closed payload namespace; policy cannot turn an extra
/// executable into an ignored metadata file or read contracts outside Git.
void validateCandidateInventory({
  required Map<String, Object?> policy,
  required Map release,
  required List<String> names,
  required List<String> archives,
  required List<String> generated,
}) {
  if ((policy['gameBuild'] as Map)['metadataFile'] !=
      '.github/robotopia-game-build.json') {
    throw StateError('Game metadata must use its fixed frozen contract path.');
  }
  const expectedGenerated = [
    'SHA256SUMS',
    'release-bom.json',
    'release-sbom.spdx.json',
    'topiaforge-update-v1.json',
    'topiaforge-update-v1.json.sig',
  ];
  if (canonicalReleaseJson(generated) !=
      canonicalReleaseJson(expectedGenerated)) {
    throw StateError(
      'Generated metadata must retain its exact release namespace.',
    );
  }
  final mods = release['mods'] as Map;
  final excluded = release['excludedDeveloperMods'] as Map;
  if (mods.keys.any(excluded.containsKey)) {
    throw StateError(
      'Excluded developer packages cannot enter release payloads.',
    );
  }
  final expected = [
    ...archives,
    for (final entry in mods.entries)
      '${entry.key}-${entry.value}.topiaforgemod',
  ]..sort();
  if (canonicalReleaseJson(names) != canonicalReleaseJson(expected)) {
    throw StateError(
      'Catalog payloads must match the exact platform and package identities.',
    );
  }
}
