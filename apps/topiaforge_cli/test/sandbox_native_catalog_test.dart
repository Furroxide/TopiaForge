import 'dart:convert';
import 'dart:io';
import 'package:test/test.dart';
import 'package:topiaforge/src/sandbox_acceptance/native_annex_verifier.dart';
import 'package:topiaforge/src/sandbox_acceptance/native_contrast.dart';
import 'package:topiaforge/src/sandbox_acceptance/native_expected_catalog.dart';
import 'package:topiaforge/src/sandbox_acceptance/native_screen_baseline.dart';
import 'package:topiaforge/src/sandbox_acceptance/sandbox_game_build.dart';
import 'sandbox_native_steps.dart';
import 'sandbox_native_transcript_fixture.dart';

/// Reviewed expected inventory (spec section 5, outcome 4), screen-baseline
/// device-profile parsing, and the WCAG contrast helper.
void main() {
  group('reviewed expected inventory', () {
    test('the shipped baseline is unreviewed and empty', () {
      final catalog = SandboxExpectedCatalog.parse(
        File(
          '../../tests/TopiaForge.SandboxAcceptanceNative/'
          'expected-catalog-v1.json',
        ).readAsBytesSync(),
      );
      expect(catalog.reviewed, isFalse);
      expect(catalog.sources, isEmpty);
      expect(catalog.entries, isEmpty);
    });

    test(
      'reviewed:false yields unavailable even when every entry is present',
      () {
        final fixture = NativeScenarioTranscript(
          'catalog-editing',
          catalog: sceneExpectedCatalog(reviewed: false),
        );
        final result = fixture.result();
        expect(result.status, SandboxNativeStatus.unavailable);
        expect(result.reason, 'expected inventory baseline not reviewed');
      },
    );

    test('a listed entry missing from the observed catalog fails', () {
      final fixture = NativeScenarioTranscript('catalog-editing')
        ..catalogForEvaluate = sceneExpectedCatalog(
          entries: const [
            {
              'rowId': 'content:dev.topiaforge.sandbox-acceptance:phantom',
              'kind': 'Prop',
              'transformCapabilities': 7,
            },
          ],
        );
      expect(fixture.result().status, SandboxNativeStatus.failed);
    });

    test('a listed source missing from the observed catalog fails', () {
      final fixture = NativeScenarioTranscript('catalog-editing')
        ..catalogForEvaluate = sceneExpectedCatalog(
          sources: const [
            {
              'id': 'ghost.source',
              'expectedState': 'Loaded',
              'minimumEntries': 1,
            },
          ],
        );
      expect(fixture.result().status, SandboxNativeStatus.failed);
    });

    test('a matching reviewed inventory passes', () {
      // The default helper matches the scene catalog and is reviewed:true.
      expect(
        NativeScenarioTranscript('catalog-editing').result().status,
        SandboxNativeStatus.passed,
      );
    });

    for (final mutation in <String, Object? Function(Map<String, Object?>)>{
      'unknown field': (j) => j..['extra'] = true,
      'wrong kind': (j) => j..['kind'] = 'other',
      'non-bool reviewed': (j) => j..['reviewed'] = 'yes',
      // A review for another build must not carry across a retarget.
      'another build': (j) => j..['gameBuild'] = sandboxGameBuild - 1,
      'bad capabilities': (j) => (j['entries'] = [
        {'rowId': 'r', 'kind': 'Prop', 'transformCapabilities': 99},
      ]),
    }.entries) {
      test('parse refuses ${mutation.key}', () {
        final json = <String, Object?>{
          'schemaVersion': 1,
          'kind': 'sandbox-native-expected-catalog-v1',
          'gameBuild': sandboxGameBuild,
          'reviewed': false,
          'sources': <Object?>[],
          'entries': <Object?>[],
        };
        mutation.value(json);
        expect(
          () => SandboxExpectedCatalog.parse(_bytes(json)),
          throwsStateError,
        );
      });
    }
  });

  group('device-profile screen baselines', () {
    const display = {'width': 1920, 'height': 1080};
    test('entries are admitted per scenario, cycle and step', () {
      final baselines = NativeScreenBaselines.parse({
        'display': display,
        'screenBaselines': {
          'root': r'D:\baselines',
          'entries': [
            {
              'scenarioId': 'routing',
              'cycle': 1,
              'step': 'open',
              'path': 'routing/open.bmp',
              'sha256': 'a' * 64,
              'tolerance': 0.01,
              'masks': <Object?>[],
            },
          ],
        },
      });
      expect(
        baselines.entryFor('routing', 1, 'open')!.path,
        'routing/open.bmp',
      );
      expect(baselines.entryFor('routing', 2, 'open'), isNull);
    });

    test('an absent screenBaselines block admits no entry', () {
      expect(
        NativeScreenBaselines.parse(const {}).entryFor('routing', 1, 'open'),
        isNull,
      );
    });

    test('a malformed entry is refused', () {
      expect(
        () => NativeScreenBaselines.parse({
          'display': display,
          'screenBaselines': {
            'root': r'D:\baselines',
            'entries': [
              {'scenarioId': 'routing', 'cycle': 1, 'step': 'open'},
            ],
          },
        }),
        throwsStateError,
      );
    });
  });

  group('WCAG relative-luminance contrast', () {
    test('black on white far exceeds 4.5:1', () {
      expect(nativeContrastMeetsAA('#000000', '#ffffff'), isTrue);
    });
    test('near-identical greys fall below 4.5:1', () {
      expect(nativeContrastMeetsAA('#808080', '#8a8a8a'), isFalse);
    });
    test('missing or malformed colours never pass', () {
      expect(nativeContrastMeetsAA('', '#ffffff'), isFalse);
      expect(nativeContrastMeetsAA('#000000', 'white'), isFalse);
      expect(nativeHasBothColours('#000000', '#ffffff'), isTrue);
      expect(nativeHasBothColours('#000000', ''), isFalse);
    });
  });
}

List<int> _bytes(Map<String, Object?> json) => utf8.encode(jsonEncode(json));
