import 'package:launcher_domain/launcher_domain.dart';
import 'package:test/test.dart';
import 'launch_preview_test_helpers.dart';

const familyId = '$ownerId.level';
const instanceId = '$familyId.one';
ResolvedPackage discoveredPackage() => previewPackage(
  worlds: [
    previewWorld(),
    const ModWorldDeclaration(
      id: familyId,
      name: 'Levels',
      content: ModWorldContent(
        kind: 'discovered',
        implementation: ModImplementationBinding(type: 'Test.Discovery'),
      ),
      transitions: ModTransitions.byPrecedence,
      spawn: ModSpawnPolicy(kind: 'provider-default'),
      openToAnyCompatible: true,
    ),
  ],
);
RuntimeObservation observation(
  EffectiveProfile profile, {
  String? id,
  int? revision,
  List<LaunchAvailability> availability = const [],
}) => RuntimeObservation.fromEnvelopes(profile, [
  LaunchObservationEnvelope(
    profileId: id ?? profile.profileId,
    profileRevision: revision ?? profile.revision,
    producer: PackageIdentity(id: ownerId, version: '1.0.0'),
    packageSetDigest: packageSetDigest(profile.packages),
    observationRevision: 1,
    discoveredWorlds: [
      DiscoveredWorldObservation(
        id: instanceId,
        familyId: familyId,
        name: 'Level One',
      ),
    ],
    availability: availability,
  ),
]);

void main() {
  test(
    'matching discovery offers concrete instance and preserves family separately',
    () {
      final profile = previewProfile([discoveredPackage()]);
      final observed = observation(profile);
      final preview = buildPreview(profile, observation: observed);
      expect(preview.worlds.map((world) => world.id), contains(instanceId));
      expect(
        preview.worlds.map((world) => world.id),
        isNot(contains(familyId)),
      );
      expect(
        preview.worlds.firstWhere((world) => world.id == instanceId).familyId,
        familyId,
      );
      final selected = buildPreview(
        profile,
        observation: observed,
        selection: LaunchSelection.target(
          LaunchRequest(targetId: targetId, worldOverride: instanceId),
        ),
      );
      expect(selected.canLaunch, isTrue);
      expect(selected.resolution!.plan!.worldId, instanceId);
      expect(selected.resolution!.plan!.worldFamilyId, familyId);
    },
  );
  test(
    'other profile and stale revision observations cannot offer instances',
    () {
      final profile = previewProfile([discoveredPackage()]);
      for (final observed in [
        observation(profile, id: 'other'),
        observation(profile, revision: 0),
      ]) {
        expect(
          buildPreview(
            profile,
            observation: observed,
          ).worlds.map((world) => world.id),
          isNot(contains(instanceId)),
        );
      }
    },
  );
  test('observation from previously enabled package cannot resurrect it', () {
    final package = discoveredPackage();
    final enabled = previewProfile([package]);
    final observed = observation(enabled);
    final disabled = previewProfile([], disabled: [package]);
    final preview = buildPreview(disabled, observation: observed);
    expect(preview.worlds, isEmpty);
    expect(preview.canLaunch, isFalse);
  });
  test(
    'admitted unavailable world remains visible with structured repair reasons',
    () {
      final profile = previewProfile([discoveredPackage()]);
      final observed = observation(
        profile,
        availability: [
          LaunchAvailability(
            kind: 'world',
            id: worldId,
            blocks: [
              const LaunchBlock(LaunchBlockCode.worldUnbound, worldId, '1.0.0'),
            ],
          ),
        ],
      );
      final preview = buildPreview(profile, observation: observed);
      expect(preview.canLaunch, isFalse);
      expect(preview.targets.single.selectable, isTrue);
      expect(
        preview.worlds.singleWhere((world) => world.id == worldId).available,
        isFalse,
      );
      expect(
        preview.worlds.singleWhere((world) => world.id == instanceId).available,
        isTrue,
      );
    },
  );
  test('longer disabled owner shadows a shorter package target in choices', () {
    final parent = previewPackage(
      targets: [previewTarget(id: '$ownerId.child.menu')],
    );
    final child = previewPackage(
      id: '$ownerId.child',
      worlds: [],
      modes: [],
      targets: [],
    );
    final preview = buildPreview(
      previewProfile([parent], disabled: [child]),
      selection: LaunchSelection.target(
        LaunchRequest(targetId: '$ownerId.child.menu'),
      ),
    );
    expect(preview.targets, isEmpty);
    expect(
      preview.blocks.map((block) => block.code),
      contains(LaunchBlockCode.targetPackageDisabled),
    );
  });
}
