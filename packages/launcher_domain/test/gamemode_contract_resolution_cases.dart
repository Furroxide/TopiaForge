// The launch-resolution half of the shared conformance fixtures. Kept apart from
// the serialization cases so both stay under the 500-line non-generated Dart cap.
//
// The resolver takes pure data, which is what makes running it from a fixture
// possible at all. The preflight it replaces read a catalog file written by a
// previous run, so it could not be reproduced from its inputs and could disagree
// with the profile actually enabled without anything noticing.

import 'dart:convert';
import 'package:launcher_domain/launcher_domain.dart';

import 'gamemode_contract_conformance_cases.dart';

/// Resolves one launch against one profile and reports what happened.
ConformanceOutcome runLaunchResolution(Map<String, Object?> body) {
  final resolution = _resolve(body);
  if (resolution.plan == null) {
    return ConformanceOutcome(
      false,
      const {},
      detail: resolution.blocks.map((block) => block.toString()).join(', '),
    );
  }

  final plan = resolution.plan!;
  return ConformanceOutcome.accepted(
    detail:
        '${plan.launchTargetId} -> ${plan.gamemodeId} in ${plan.worldId} '
        'via ${plan.transition}',
  );
}

/// Exact full tuples and descriptor values are normative, not a set of codes.
Map<String, Object?> resolutionSnapshot(Map<String, Object?> body) {
  final first = _resolutionSnapshot(body);
  final reversed = jsonDecode(jsonEncode(body)) as Map<String, Object?>;
  final profile = reversed['profile']! as Map;
  for (final key in ['packages', 'disabledPackages']) {
    if (profile[key] is List) {
      profile[key] = (profile[key] as List).reversed.toList();
    }
  }
  final observation = reversed['observation'] as Map?;
  if (observation != null) {
    observation['envelopes'] = (observation['envelopes'] as List).reversed
        .toList();
  }
  if (jsonEncode(first) != jsonEncode(_resolutionSnapshot(reversed))) {
    throw StateError(
      'Resolution changes with input package/observation order.',
    );
  }
  return first;
}

Map<String, Object?> _resolutionSnapshot(Map<String, Object?> body) {
  final result = _resolve(body);
  if (result.plan == null) {
    return {
      'outcome': 'reject',
      'blocks': result.blocks
          .map(
            (block) => {
              'code': block.code.name,
              'subject': block.subject,
              'subjectVersion': block.subjectVersion,
            },
          )
          .toList(),
    };
  }
  return {'outcome': 'accept', 'normalized': result.plan!.toJson()};
}

LaunchResolution _resolve(Map<String, Object?> body) {
  final profile = body['profile']! as Map<String, Object?>;
  final observation = body['observation'] as Map<String, Object?>?;
  final effective = EffectiveProfile(
    profileId: (profile['profileId'] as String?) ?? 'fixture',
    revision: (profile['revision'] as int?) ?? 1,
    packages: _packages(profile['packages']),
    disabledPackages: _packages(profile['disabledPackages']),
    install: _install(profile['install'] as Map<String, Object?>?),
  );
  return LaunchResolver.resolve(
    effective,
    _request(body['request']! as Map<String, Object?>),
    observation == null
        ? RuntimeObservation.none
        : RuntimeObservation.fromEnvelopes(
            effective,
            (observation['envelopes']! as List).map(
              LaunchObservationEnvelope.fromJson,
            ),
          ),
    body['bindings'] == null
        ? null
        : _bindings(body['bindings']! as Map<String, Object?>),
  );
}

RuntimeBindingSnapshot _bindings(Map<String, Object?> raw) =>
    RuntimeBindingSnapshot(
      profileId: raw['profileId']! as String,
      profileRevision: raw['profileRevision']! as int,
      packageSetDigest: raw['packageSetDigest']! as String,
      boundWorldIds: (raw['boundWorldIds']! as List).cast<String>(),
      boundGamemodeIds: (raw['boundGamemodeIds']! as List).cast<String>(),
      availability: ((raw['availability'] as List?) ?? const []).map(
        LaunchAvailability.fromJson,
      ),
    );

List<ResolvedPackage> _packages(Object? value) {
  if (value is! List) return const [];
  return value.cast<Map<String, Object?>>().map((item) {
    final manifest = ModManifest.fromJson(
      item['manifest']! as Map<String, Object?>,
    );
    final validation = item['validation']! as Map;
    final codes = manifest
        .validate()
        .where((issue) => issue.isBlocking)
        .map((issue) => issue.message.split(' ').first)
        .toSet();
    final expected = ((validation['errorCodes'] as List?) ?? const [])
        .cast<String>()
        .toSet();
    if (codes.length != expected.length ||
        !codes.containsAll(expected) ||
        codes.isEmpty != (validation['outcome'] == 'accept')) {
      throw StateError(
        'Resolution input manifest ${item['id']} validation differs: $codes vs $expected',
      );
    }
    return ResolvedPackage(
      id: item['id']! as String,
      version: item['version']! as String,
      manifest: manifest,
    );
  }).toList();
}

InstallFacts _install(Map<String, Object?>? value) => value == null
    ? const InstallFacts()
    : InstallFacts(
        platform: (value['platform'] as String?) ?? '',
        architecture: (value['architecture'] as String?) ?? '',
        contentTarget: (value['contentTarget'] as String?) ?? '',
        gameVersion: (value['gameVersion'] as String?) ?? '',
      );

LaunchRequest _request(Map<String, Object?> value) => LaunchRequest(
  targetId: (value['targetId'] as String?) ?? '',
  worldOverride: value['worldOverride'] as String?,
  transitionOverride: value['transitionOverride'] as String?,
);

/// Plans must not retain caller-owned package/manifest objects under a fixed digest.
bool resolutionCopiesPackageIdentities(Map<String, Object?> body) {
  final raw = body['profile']! as Map<String, Object?>;
  final packages = _packages(raw['packages']);
  final profile = EffectiveProfile(
    profileId: 'fixture',
    revision: 1,
    packages: packages,
  );
  final result = LaunchResolver.resolve(
    profile,
    _request(body['request']! as Map<String, Object?>),
  );
  final plan = result.plan!;
  return plan.packages.every(
    (identity) => !packages.any((input) => identical(identity, input)),
  );
}
