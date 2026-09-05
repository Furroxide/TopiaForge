import 'package:launcher_domain/launcher_domain.dart';
import 'package:test/test.dart';

void registerLaunchTransportInvariants() {
  final package = PackageIdentity(id: 'base.mod', version: '1.0.0');
  final digest = packageSetDigest([package]);
  final ids = List.generate(4097, (i) => 'base.mod.world$i');
  final packages = List.generate(
    4097,
    (i) => PackageIdentity(id: 'base.mod$i', version: '1.0.0'),
  );
  final blocks = ids
      .map((id) => LaunchBlock(LaunchBlockCode.worldUnavailable, id))
      .toList();
  final discoveries = ids
      .map(
        (id) => DiscoveredWorldObservation(
          id: '$id.instance',
          familyId: id,
          name: 'World',
        ),
      )
      .toList();
  final availability = ids
      .map(
        (id) =>
            LaunchAvailability(kind: 'world', id: id, blocks: [blocks.first]),
      )
      .toList();
  LaunchPlanDescriptor plan(Iterable<PackageIdentity> selected) =>
      LaunchPlanDescriptor(
        targetId: 'base.mod.menu',
        gamemodeId: 'base.mod.mode',
        worldId: 'base.mod.world',
        transition: 'scene-replacement',
        request: LaunchRequest(targetId: 'base.mod.menu'),
        packages: selected,
      );
  ProfileLaunchConfigurationV4 profile(
    Iterable<String> enabled,
    Map<String, String> versions,
  ) => ProfileLaunchConfigurationV4(
    profileId: 'profile',
    profileRevision: 0,
    requestId: 'request',
    command: 'main-menu',
    safeMode: false,
    inheritManagerModState: true,
    enabledMods: enabled,
    selectedVersions: versions,
    packages: [package],
  );
  LaunchOutcome outcome(Iterable<LaunchBlock> reasons) => LaunchOutcome(
    kind: 'launch',
    requestId: 'request',
    sequence: 0,
    phase: 'preparing',
    status: 'failed',
    blocks: reasons,
    command: 'launch-target',
  );
  LaunchObservationEnvelope observation(
    Iterable<DiscoveredWorldObservation> worlds,
    Iterable<LaunchAvailability> reasons,
  ) => LaunchObservationEnvelope(
    profileId: 'profile',
    profileRevision: 0,
    producer: package,
    packageSetDigest: digest,
    observationRevision: 0,
    discoveredWorlds: worlds,
    availability: reasons,
  );
  RuntimeBindingSnapshot bindings(
    Iterable<String> worlds,
    Iterable<String> modes,
    Iterable<LaunchAvailability> reasons,
  ) => RuntimeBindingSnapshot(
    profileId: 'profile',
    profileRevision: 0,
    packageSetDigest: digest,
    boundWorldIds: worlds,
    boundGamemodeIds: modes,
    availability: reasons,
  );
  test('transport constructors accept exactly4096 collection entries', () {
    plan(packages.take(4096));
    profile(packages.take(4096).map((p) => p.id), {});
    profile([], {for (final p in packages.take(4096)) p.id: p.version});
    outcome(blocks.take(4096));
    observation(discoveries.take(4096), []);
    observation([], availability.take(4096));
    bindings(ids.take(4096), ids.take(4096), []);
    bindings([], [], availability.take(4096));
  });
  final invalid = <String, void Function()>{
    '4097 plan packages': () => plan(packages),
    '4097 enabled ids': () => profile(packages.map((p) => p.id), {}),
    '4097 selected versions': () =>
        profile([], {for (final p in packages) p.id: p.version}),
    '4097 outcome blocks': () => outcome(blocks),
    '4097 duplicate outcome blocks': () =>
        outcome(List.filled(4097, blocks.first)),
    '4097 availability blocks': () =>
        LaunchAvailability(kind: 'world', id: ids.first, blocks: blocks),
    '4097 discovered worlds': () => observation(discoveries, []),
    '4097 availability records': () => observation([], availability),
    '4097 world binding ids': () => bindings(ids, [], []),
    '4097 mode binding ids': () => bindings([], ids, []),
    '4097 binding failures': () => bindings([], [], availability),
    'duplicate world bindings': () =>
        bindings([ids.first, ids.first.toUpperCase()], [], []),
    'duplicate mode bindings': () =>
        bindings([], [ids.first, ids.first.toUpperCase()], []),
    'duplicate binding availability': () =>
        bindings([], [], [availability.first, availability.first]),
    'empty operation error': () =>
        LaunchOperationError(code: 'external', message: ''),
  };
  for (final entry in invalid.entries) {
    test(
      'transport constructors reject ${entry.key}',
      () => expect(entry.value, throwsFormatException),
    );
  }
  for (final suffix in ['\n', '\r', '\t', 'é']) {
    test(
      'transport constructor token/version suffix ${suffix.codeUnitAt(0)} rejected',
      () {
        expect(
          () => PackageIdentity(id: 'base.mod', version: '1.0.0$suffix'),
          throwsFormatException,
        );
        expect(
          () => profile([], {'other.mod': '1.0.0$suffix'}),
          throwsFormatException,
        );
        expect(
          () => LaunchProgress(
            requestId: 'request$suffix',
            sequence: 0,
            phase: 'idle',
          ),
          throwsFormatException,
        );
      },
    );
  }
}
