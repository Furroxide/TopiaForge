import 'package:launcher_domain/launcher_domain.dart';
import 'package:test/test.dart';

String declarationId(int length) => 'mode.${'x' * (length - 5)}';

void main() {
  for (final length in [65, 96]) {
    final id = declarationId(length);
    test('profile persists and writes $length-character launch IDs', () {
      final profile = LauncherProfile(
        id: 'boundary',
        name: 'Boundary',
        launchSelection: LaunchSelection.target(
          LaunchRequest(targetId: id, worldOverride: id),
        ),
      );
      final restored = LauncherProfile.fromJson(profile.toJson());
      expect(restored.launchSelection.request!.targetId, id);
      expect(restored.launchSelection.request!.worldOverride, id);
      final plan = LaunchPlanDescriptor(
        targetId: id,
        gamemodeId: id,
        worldId: id,
        transition: ModTransitions.additiveArena,
        request: restored.launchSelection.request!,
        packages: [],
      );
      final wire = ProfileLaunchConfigurationV4(
        profileId: profile.id,
        profileRevision: 0,
        requestId: 'boundary-request',
        command: 'launch-target',
        safeMode: false,
        inheritManagerModState: false,
        enabledMods: [],
        selectedVersions: {},
        packages: [],
        plan: plan,
      );
      final read = LaunchTransport.readProfile(
        LaunchTransport.writeProfile(wire),
      );
      expect(read.plan!.targetId, id);
      expect(read.plan!.worldId, id);
      expect(read.plan!.gamemodeId, id);
    });
  }

  for (final id in [declarationId(97), 'mode.é']) {
    test('invalid declaration $id is rejected across launch paths', () {
      expect(() => LaunchRequest(targetId: id), throwsFormatException);
      expect(
        () => LaunchRequest(targetId: 'valid.target', worldOverride: id),
        throwsFormatException,
      );
      expect(
        () => LauncherProfile.fromJson({
          'launchSelection': {
            'schemaVersion': 1,
            'kind': 'target',
            'request': {'targetId': id},
          },
        }),
        throwsFormatException,
      );
    });
  }

  test('package identities retain their 64-character boundary', () {
    final legal = declarationId(64);
    final illegal = declarationId(65);
    expect(ModManifest.isValidId(legal), isTrue);
    expect(ModManifest.isValidId(illegal), isFalse);
    for (final profile in [
      LauncherProfile(id: 'enabled', name: 'Enabled', enabledMods: {illegal}),
      LauncherProfile(
        id: 'pinned',
        name: 'Pinned',
        selectedVersions: {illegal: '1.0.0'},
      ),
    ]) {
      expect(
        () => ProfileLaunchConfigurationV4(
          profileId: profile.id,
          profileRevision: 0,
          requestId: 'boundary-request',
          command: 'main-menu',
          safeMode: false,
          inheritManagerModState: false,
          enabledMods: profile.enabledMods,
          selectedVersions: profile.selectedVersions,
          packages: [],
        ),
        throwsFormatException,
      );
    }
  });
}
