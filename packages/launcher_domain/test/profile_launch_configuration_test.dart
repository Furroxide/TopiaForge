import 'package:launcher_domain/launcher_domain.dart';
import 'package:test/test.dart';

void main() {
  group('ProfileLaunchConfiguration', () {
    test('keeps profile mod and version selections isolated', () {
      final firstEnabled = <String>{'alpha.mod'};
      final first = ProfileLaunchConfiguration.fromProfile(
        LauncherProfile(
          id: 'first',
          name: 'First',
          enabledMods: firstEnabled,
          selectedVersions: const {'alpha.mod': '1.0.0'},
        ),
      );
      final second = ProfileLaunchConfiguration.fromProfile(
        const LauncherProfile(
          id: 'second',
          name: 'Second',
          enabledMods: {'beta.mod'},
          selectedVersions: {'beta.mod': '2.0.0'},
        ),
      );

      firstEnabled.add('beta.mod');

      expect(first.enabledMods, {'alpha.mod'});
      expect(first.selectedVersions, {'alpha.mod': '1.0.0'});
      expect(second.enabledMods, {'beta.mod'});
      expect(second.selectedVersions, {'beta.mod': '2.0.0'});
      expect(first.toJson()['schemaVersion'], 2);
    });

    test('distinguishes exact empty profiles from manager inheritance', () {
      const exact = LauncherProfile(id: 'empty', name: 'Empty');
      final restored = LauncherProfile.fromJson(exact.toJson());
      final missingFlag = LauncherProfile.fromJson(const {
        'id': 'missing-flag',
        'name': 'Missing flag',
        'enabledMods': <Object?>[],
      });

      expect(restored.inheritManagerModState, isFalse);
      expect(
        ProfileLaunchConfiguration.fromProfile(restored).inheritManagerModState,
        isFalse,
      );
      expect(missingFlag.inheritManagerModState, isFalse);
      expect(LauncherProfile.defaultProfile().inheritManagerModState, isTrue);
    });

    test('carries safe mode while keeping launch environment process-only', () {
      const environment = {'TOPIAFORGE_PROFILE_TEST': 'safe'};
      const profile = LauncherProfile(
        id: 'safe',
        name: 'Safe',
        launchSettings: LaunchSettings(
          safeMode: true,
          environment: environment,
        ),
      );
      final configuration = ProfileLaunchConfiguration.fromProfile(profile);

      expect(configuration.safeMode, isTrue);
      expect(configuration.toJson(), isNot(contains('environment')));
      expect(profile.launchSettings.environment, environment);
    });

    test('rejects unsafe ids and malformed selected versions', () {
      for (final profile in [
        const LauncherProfile(id: 'unsafe\nprofile', name: 'Unsafe profile id'),
        const LauncherProfile(
          id: 'unsafe-id',
          name: 'Unsafe',
          enabledMods: {'../escape'},
        ),
        const LauncherProfile(
          id: 'bad-version',
          name: 'Bad version',
          selectedVersions: {'alpha.mod': '1.0'},
        ),
      ]) {
        expect(
          () => ProfileLaunchConfiguration.fromProfile(profile),
          throwsFormatException,
        );
      }
    });

    test('rejects unsafe in-memory world and gamemode ids', () {
      final retired =
          'robo'
          'topia.world.retired';
      for (final selection in [
        WorldSelection(worldId: retired),
        WorldSelection(gamemodeId: retired),
      ]) {
        expect(
          () => ProfileLaunchConfiguration.fromProfile(
            LauncherProfile(
              id: 'unsafe-world',
              name: 'Unsafe world',
              worldSelection: selection,
            ),
          ),
          throwsFormatException,
        );
      }
    });
  });
}
