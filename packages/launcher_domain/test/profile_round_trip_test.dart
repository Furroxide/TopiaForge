import 'package:launcher_domain/launcher_domain.dart';
import 'package:test/test.dart';

void main() {
  group('LauncherProfile', () {
    test('round trips durable profile state', () {
      final profile = LauncherProfile(
        id: 'speedrun',
        name: 'Speedrun',
        enabledMods: {'timer.mod'},
        selectedVersions: {'timer.mod': '2.0.0'},
        launchSettings: const LaunchSettings(
          safeMode: true,
          extraArguments: ['-screen-fullscreen', '0'],
        ),
      );

      final restored = LauncherProfile.fromJson(profile.toJson());

      expect(restored.id, profile.id);
      expect(restored.enabledMods, contains('timer.mod'));
      expect(restored.selectedVersions['timer.mod'], '2.0.0');
      expect(restored.launchSettings.safeMode, isTrue);
    });

    test('round trips legacy selection without inferring a target', () {
      const selection = WorldSelection(
        worldId: 'io.github.furroxide.topiaforge.worlds.level.city',
        gamemodeId: 'io.github.furroxide.topiaforge.zombies.survival',
        loadMode: WorldSelection.sceneReplacement,
        launchIntoGamemode: true,
      );
      final profile = LauncherProfile(
        id: 'p',
        name: 'P',
        worldSelection: selection,
      );

      final restored = LauncherProfile.fromJson(profile.toJson());

      expect(
        restored.launchSelection.kind,
        LaunchSelectionKind.unresolvedLegacy,
      );
      expect(restored.launchSelection.legacy, {
        'worldSelection': selection.toJson(),
      });
    });

    test('profile parsing preserves runtime-only legacy keys for repair', () {
      final profile = LauncherProfile.fromJson({
        'worldSelection': {
          'selectedWorldId': 'retired-world',
          'selectedGamemodeId': 'retired-mode',
        },
      });

      expect(profile.launchSelection.legacy, {
        'worldSelection': {
          'selectedWorldId': 'retired-world',
          'selectedGamemodeId': 'retired-mode',
        },
      });
    });

    test(
      'profile parsing retains retired IDs as unavailable legacy values',
      () {
        final legacy = {
          'worldSelection': {
            'worldId':
                'robo'
                'topia.world.old',
          },
        };
        final profile = LauncherProfile.fromJson(legacy);
        expect(
          profile.launchSelection.kind,
          LaunchSelectionKind.unresolvedLegacy,
        );
        expect(profile.launchSelection.legacy, legacy);
      },
    );
  });
}
