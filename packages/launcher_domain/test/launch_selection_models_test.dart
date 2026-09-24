import 'dart:convert';
import 'package:launcher_domain/launcher_domain.dart';
import 'package:test/test.dart';
import 'launch_preview_test_helpers.dart';

void main() {
  test('legacy nested values are copied and deeply immutable', () {
    final nested = <Object?>[1, null, false];
    final raw = <String, Object?>{
      'worldSelection': {'extension': nested},
    };
    final selection = LaunchSelection.unresolvedLegacy(raw);
    nested.clear();
    raw.clear();
    expect(((selection.legacy!['worldSelection'] as Map)['extension']), [
      1,
      null,
      false,
    ]);
    expect(() => selection.legacy!.clear(), throwsUnsupportedError);
    expect(
      () => ((selection.legacy!['worldSelection'] as Map)['extension'] as List)
          .clear(),
      throwsUnsupportedError,
    );
  });
  test(
    'selection equality is structural while preserving property presence',
    () {
      final a = LaunchSelection.unresolvedLegacy({
        'x': 1,
        'y': [false],
      });
      final b = LaunchSelection.unresolvedLegacy({
        'y': [false],
        'x': 1,
      });
      expect(a, b);
      expect(a.hashCode, b.hashCode);
      expect(
        LaunchSelection.unresolvedLegacy({}),
        isNot(LaunchSelection.unresolvedLegacy({'worldSelection': null})),
      );
    },
  );
  test('new profiles start at explicit main menu', () {
    expect(
      LauncherProfile.defaultProfile().launchSelection,
      const LaunchSelection.mainMenu(),
    );
  });
  for (final length in [65, 96, 97]) {
    test('durable target identity boundary $length', () {
      final value = {
        'schemaVersion': 1,
        'kind': 'target',
        'request': {'targetId': 'x' * length},
      };
      if (length > 96) {
        expect(() => LaunchSelection.fromJson(value), throwsFormatException);
      } else {
        final selection = LaunchSelection.fromJson(value);
        expect(
          LaunchSelection.fromJson(jsonDecode(jsonEncode(selection.toJson()))),
          selection,
        );
      }
    });
  }
  for (final value in [
    {'schemaVersion': 2, 'kind': 'main-menu'},
    {'schemaVersion': 1, 'kind': 'future'},
    {'schemaVersion': 1, 'kind': 'main-menu', 'request': null},
    {
      'schemaVersion': 1,
      'kind': 'target',
      'request': {'targetId': 'example.mode', 'worldOverride': null},
    },
  ]) {
    test('durable selection rejects unrecognized contract $value', () {
      expect(() => LaunchSelection.fromJson(value), throwsFormatException);
    });
  }
  test(
    'legacy maps only a unique target with identical world and transition',
    () {
      final original = legacySelection();
      final single = LegacySelectionResolution.resolve(
        previewProfile([previewPackage()]),
        original,
      );
      expect(single.request!.targetId, targetId);
      expect(single.request!.transitionOverride, ModTransitions.additiveArena);
      expect(original.legacy!['worldSelection'], isNotNull);
      final ambiguous = LegacySelectionResolution.resolve(
        previewProfile([
          previewPackage(
            targets: [
              previewTarget(),
              previewTarget(id: '$ownerId.second'),
            ],
          ),
        ]),
        original,
      );
      expect(ambiguous, original);
    },
  );
  test('retired Sandbox mode is never remapped to Free Play', () {
    final original = legacySelection(
      gamemode: 'io.github.furroxide.topiaforge.worlds.sandbox',
    );
    expect(
      LegacySelectionResolution.resolve(
        previewProfile([previewPackage()]),
        original,
      ),
      original,
    );
  });
  test('unknown transition and absent fields remain unresolved', () {
    for (final original in [
      legacySelection(transition: 'future'),
      LaunchSelection.unresolvedLegacy({
        'worldSelection': {'launchIntoGamemode': true},
      }),
    ]) {
      expect(
        LegacySelectionResolution.resolve(
          previewProfile([previewPackage()]),
          original,
        ),
        original,
      );
    }
  });
  test(
    'legacy false uses main menu without changing remembered raw values',
    () {
      final raw = {
        'worldSelection': {
          'launchIntoGamemode': false,
          'gamemodeId': 'retired.mode',
          'loadMode': 'future',
        },
      };
      final original = LaunchSelection.unresolvedLegacy(raw);
      expect(
        LegacySelectionResolution.resolve(previewProfile([]), original).kind,
        LaunchSelectionKind.mainMenu,
      );
      expect(original.legacy, raw);
    },
  );
  test('preview copies package identities and option collections', () {
    final preview = buildPreview(previewProfile([previewPackage()]));
    expect(preview.packages.single, isNot(isA<ResolvedPackage>()));
    expect(() => preview.packages.clear(), throwsUnsupportedError);
    expect(() => preview.targets.clear(), throwsUnsupportedError);
    expect(() => preview.worlds.clear(), throwsUnsupportedError);
    expect(() => preview.transitions.clear(), throwsUnsupportedError);
  });
}

LaunchSelection legacySelection({
  String gamemode = modeId,
  String transition = 'additiveArena',
}) => LaunchSelection.unresolvedLegacy({
  'worldSelection': {
    'worldId': worldId,
    'gamemodeId': gamemode,
    'loadMode': transition,
    'launchIntoGamemode': true,
  },
});
