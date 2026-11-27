import 'package:launcher_domain/launcher_domain.dart';
import 'package:test/test.dart';

void main() {
  for (final source in <Map<String, Object?>>[
    {},
    {'id': 'old', 'name': 'Old profile'},
    {'enabledMods': <String>[]},
    {'launchSelection': LaunchSelection.unresolvedLegacy({}).toJson()},
  ]) {
    test(
      'profile without an actual remembered choice uses main menu: $source',
      () {
        final profile = LauncherProfile.fromJson(source);
        expect(profile.launchSelection.kind, LaunchSelectionKind.mainMenu);
        expect(
          profile.copyWith(name: 'Renamed').launchSelection.kind,
          LaunchSelectionKind.mainMenu,
        );
        expect(
          LauncherProfile.fromJson(profile.toJson()).launchSelection.kind,
          LaunchSelectionKind.mainMenu,
        );
      },
    );
  }

  test('empty programmatic legacy selection uses main menu', () {
    final profile = LauncherProfile(
      id: 'p',
      name: 'P',
      launchSelection: LaunchSelection.unresolvedLegacy({}),
    );
    expect(profile.launchSelection.kind, LaunchSelectionKind.mainMenu);
  });

  for (final value in <Object?>[
    null,
    <String, Object?>{},
    <Object?>[],
    {'gamemodeId': 'io.github.furroxide.topiaforge.worlds.sandbox'},
    {'launchIntoGamemode': true, 'worldId': 'missing.world'},
  ]) {
    test('present legacy value is retained for repair: $value', () {
      final profile = LauncherProfile.fromJson({'worldSelection': value});
      expect(
        profile.launchSelection.kind,
        LaunchSelectionKind.unresolvedLegacy,
      );
      expect(profile.launchSelection.legacy, {'worldSelection': value});
      expect(
        LauncherProfile.fromJson(profile.toJson()).launchSelection.legacy,
        {'worldSelection': value},
      );
    });
  }

  test('explicit malformed selection is not treated as absent', () {
    expect(
      () => LauncherProfile.fromJson({'launchSelection': null}),
      throwsFormatException,
    );
  });
}
