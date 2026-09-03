// The launch-resolution half of the shared conformance fixtures. Kept apart from
// the serialization cases so both stay under the 500-line non-generated Dart cap.
//
// The resolver takes pure data, which is what makes running it from a fixture
// possible at all. The preflight it replaces read a catalog file written by a
// previous run, so it could not be reproduced from its inputs and could disagree
// with the profile actually enabled without anything noticing.

import 'package:launcher_domain/launcher_domain.dart';

import 'gamemode_contract_conformance_cases.dart';

/// Resolves one launch against one profile and reports what happened.
ConformanceOutcome runLaunchResolution(Map<String, Object?> body) {
  final resolution = _resolve(body);
  if (resolution.plan == null) {
    return ConformanceOutcome(
      false,
      resolution.blocks.map((block) => block.code.name).toSet(),
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

/// Renders a plan in the shared shape both runners compare.
Map<String, Object?> launchPlanDigest(Map<String, Object?> body) {
  final plan = _resolve(body).plan!;
  return {
    'launchTargetId': plan.launchTargetId,
    'gamemodeId': plan.gamemodeId,
    'worldId': plan.worldId,
    'worldInstanceId': plan.worldInstanceId,
    'transition': plan.transition,
    'resolvedPackages': plan.resolvedPackages
        .map((item) => '${item.id}@${item.version}')
        .toList(),
  };
}

/// Whether a plan's digest accepts the set it was built from and rejects a
/// different one.
///
/// Checked as a property rather than pinned as a value: the fixtures should not
/// have to carry a hash the two languages agree on by coincidence, and what the
/// revalidation before preparing actually needs is that the comparison
/// discriminates.
bool digestAgreesWithItsPackages(Map<String, Object?> body) {
  final plan = _resolve(body).plan!;
  return LaunchResolver.revalidate(plan, plan.resolvedPackages).isEmpty &&
      LaunchResolver.revalidate(plan, const []).length == 1;
}

LaunchResolution _resolve(Map<String, Object?> body) {
  final profile = body['profile']! as Map<String, Object?>;
  final observation = body['observation'] as Map<String, Object?>?;
  return LaunchResolver.resolve(
    EffectiveProfile(
      profileId: 'fixture',
      revision: 1,
      packages: _packages(profile['packages']),
      disabledPackages: _packages(profile['disabledPackages']),
      install: _install(profile['install'] as Map<String, Object?>?),
    ),
    _request(body['request']! as Map<String, Object?>),
    observation == null
        ? RuntimeObservation.none
        : RuntimeObservation(
            unavailableWorldIds: _strings(observation['unavailableWorldIds']),
            discoveredWorldIds: _strings(observation['discoveredWorldIds']),
            unboundGamemodeIds: _strings(observation['unboundGamemodeIds']),
          ),
  );
}

List<ResolvedPackage> _packages(Object? value) {
  if (value is! List) {
    return const [];
  }
  return value
      .cast<Map<String, Object?>>()
      .map(
        (item) => ResolvedPackage(
          id: item['id']! as String,
          version: item['version']! as String,
          manifest: ModManifest.fromJson(
            item['manifest']! as Map<String, Object?>,
          ),
        ),
      )
      .toList();
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
  worldOverride: (value['worldOverride'] as String?) ?? '',
  transitionOverride: (value['transitionOverride'] as String?) ?? '',
);

List<String> _strings(Object? value) =>
    value is List ? value.cast<String>() : const [];
