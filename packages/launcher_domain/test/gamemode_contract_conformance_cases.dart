// Case bodies for the cross-language gamemode-contract fixtures. Kept out of the
// test file so both stay under the 500-line non-generated Dart cap, which applies
// to tests as well: the audit globs `git ls-files '*.dart'` with no exclusion.
//
// Not named `*_test.dart`, so `dart test` does not pick it up as a suite of its
// own; the runner in gamemode_contract_conformance_test.dart imports it.

import 'package:launcher_domain/launcher_domain.dart';

import 'gamemode_contract_conformance_model_mutations.dart';

/// What one runner did with one fixture. `errorCodes` is compared as a set, not
/// as a count or a prefix -- comparing only the accept/reject verdict is what let
/// the two hand-written readers drift in the first place.
class ConformanceOutcome {
  const ConformanceOutcome(this.accepted, this.errorCodes, {this.detail = ''});

  const ConformanceOutcome.accepted({String detail = ''})
    : this(true, const <String>{}, detail: detail);

  final bool accepted;
  final Set<String> errorCodes;

  /// Extra context for the failure message. Never compared.
  final String detail;
}

/// Reads a whole manifest exactly as the launcher does, and renders a digest of
/// what came out.
///
/// The digest is the point. The older shared-manifest corpus compares only the
/// accept/reject verdict, so two readers can agree a manifest is valid while
/// disagreeing about what it said -- and for an absent flag against an explicit
/// false, that disagreement is a behaviour change nothing would report.
ConformanceOutcome runManifest(Map<String, Object?> body) {
  final json = body['manifest']! as Map<String, Object?>;
  ModManifest manifest;
  try {
    manifest = ModManifest.fromJson(json);
    if (body['modelMutation'] case final String mutation) {
      manifest = mutateConformanceModel(manifest, mutation);
    }
  } on FormatException catch (error) {
    return ConformanceOutcome(false, {
      _codeFor(error.message),
    }, detail: 'the reader threw: ${error.message}');
  } on TypeError catch (error) {
    return ConformanceOutcome(false, const {
      'unreadable',
    }, detail: 'the reader could not bind the document: $error');
  }

  final blocking = manifest
      .validate()
      .where((issue) => issue.isBlocking)
      .toList(growable: false);
  if (blocking.isNotEmpty) {
    return ConformanceOutcome(
      false,
      blocking.map((issue) => _codeFor(issue.message)).toSet(),
      detail: blocking.map((issue) => issue.message).join(' | '),
    );
  }
  return ConformanceOutcome.accepted(detail: 'accepted');
}

/// Every complaint opens with the field it is about, so the leading token is a
/// stable code without inventing a second vocabulary the prose could drift
/// from. The C# runner projects its own messages the same way.
String _codeFor(String message) {
  final space = message.indexOf(' ');
  return space > 0 ? message.substring(0, space) : message;
}

/// Normalize every typed contribution field, including display metadata and presence.
Map<String, Object?> declarationDigest(Map<String, Object?> body) =>
    normalizeContributions(
      ModManifest.fromJson(
            body['manifest']! as Map<String, Object?>,
          ).contributions?.toJson() ??
          const {},
    );

Map<String, Object?> normalizeContributions(Map<String, Object?> source) => {
  for (final kind in const ['worlds', 'gamemodes', 'launchTargets'])
    kind: ((source[kind] as List?) ?? const [])
        .map((item) => _normalize(item as Map, kind))
        .toList(),
};

Map<String, Object?> _normalize(Map source, String kind) {
  final fields = contributionNormalizationFields[kind]!;
  for (final key in source.keys) {
    if (!fields.contains(key)) {
      throw StateError('Normalization is missing $kind.$key');
    }
  }
  return {
    for (final field in fields)
      field:
          source[field] is Map &&
              contributionNormalizationFields.containsKey(field)
          ? _normalize(source[field] as Map, field)
          : source[field],
  };
}

const contributionNormalizationFields = {
  'worlds': [
    'id',
    'name',
    'description',
    'content',
    'transitions',
    'spawn',
    'openTo',
    'openToAnyCompatible',
  ],
  'gamemodes': [
    'id',
    'name',
    'description',
    'implementation',
    'worldRequirements',
    'sceneChangePolicy',
  ],
  'launchTargets': [
    'id',
    'title',
    'description',
    'sortKey',
    'gamemode',
    'world',
    'transition',
  ],
  'content': ['kind', 'bundle', 'prefab', 'implementation', 'sceneName'],
  'implementation': ['assembly', 'type'],
  'spawn': ['kind', 'markerName'],
  'worldRequirements': ['transitions', 'spawn'],
  'world': ['policy', 'default', 'allow', 'allowPlayerOverride'],
};

Map<String, Object?> roundTripDigest(Map<String, Object?> body) {
  final original = body['manifest']! as Map<String, Object?>;
  final parsed = ModManifest.fromJson(original);
  final serialized = <String, Object?>{
    ...original,
    if (parsed.contributions != null)
      'contributions': parsed.contributions!.toJson(),
  };
  final restored = ModManifest.fromJson(serialized);
  final failures = restored.validate().where((issue) => issue.isBlocking);
  if (failures.isNotEmpty) {
    throw StateError('Serialized contributions fail validation: $failures');
  }
  return normalizeContributions(restored.contributions?.toJson() ?? const {});
}

bool deepEquals(Object? left, Object? right) {
  if (left is Map && right is Map) {
    if (left.length != right.length) {
      return false;
    }
    for (final key in left.keys) {
      if (!right.containsKey(key) || !deepEquals(left[key], right[key])) {
        return false;
      }
    }
    return true;
  }
  if (left is List && right is List) {
    if (left.length != right.length) {
      return false;
    }
    for (var index = 0; index < left.length; index++) {
      if (!deepEquals(left[index], right[index])) {
        return false;
      }
    }
    return true;
  }
  return left == right;
}
