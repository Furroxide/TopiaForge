import 'dart:typed_data';
import 'package:crypto/crypto.dart';
import 'package:test/test.dart';
import 'package:topiaforge/src/sandbox_acceptance/native_driver_vocabulary.dart';
import 'package:topiaforge/src/sandbox_acceptance/native_screen_baseline.dart';
import 'package:topiaforge/src/sandbox_acceptance/native_screen_bitmap.dart';
import 'sandbox_native_screen_fixture.dart';

/// Baseline arithmetic, the strict BMP decoder and admitted-profile parsing
/// with synthetic pixels, mirroring the broker's `ScreenBaselineTests`.
void main() {
  group('mismatch arithmetic', () {
    const width = 4, height = 4;
    final capture = Uint8List(width * height * 4);
    final baseline = Uint8List(width * height * 4);
    baseline[0] = 255; // (0,0): blue differs
    baseline[15 * 4 + 2] = 200; // (3,3): red differs
    baseline[5 * 4 + 3] = 255; // (1,1): alpha only, ignored
    baseline[6 * 4 + 1] = nativeScreenChannelThreshold; // (2,1): not above it
    baseline[7 * 4 + 1] = nativeScreenChannelThreshold + 1; // (3,1): above it
    double fraction([List<NativeScreenMask> masks = const []]) =>
        nativeScreenMismatchFraction(capture, baseline, width, height, masks);

    test('counts pixels whose largest B/G/R difference exceeds 8', () {
      expect(fraction(), 3 / 16);
    });
    test('a mask removes its pixel from numerator and denominator', () {
      expect(fraction([const NativeScreenMask(0, 0, 1, 1)]), 2 / 15);
    });
    test('masks covering every differing pixel yield zero', () {
      expect(
        fraction(const [
          NativeScreenMask(0, 0, 1, 1),
          NativeScreenMask(2, 1, 2, 3),
        ]),
        0,
      );
    });
    test('overlapping masks count once', () {
      expect(
        fraction(const [
          NativeScreenMask(0, 0, 2, 2),
          NativeScreenMask(1, 1, 2, 2),
        ]),
        2 / 9,
      );
    });
    test('full coverage, escaping or empty masks and size mismatch refuse', () {
      for (final mask in const [
        NativeScreenMask(0, 0, 4, 4),
        NativeScreenMask(3, 3, 2, 1),
        NativeScreenMask(-1, 0, 1, 1),
        NativeScreenMask(0, 0, 0, 1),
      ]) {
        expect(() => fraction([mask]), throwsStateError);
      }
      expect(
        () => nativeScreenMismatchFraction(
          capture,
          Uint8List(60),
          width,
          height,
          const [],
        ),
        throwsStateError,
      );
    });
  });

  group('strict BMP decoder', () {
    test('round-trips the broker layout', () {
      final pixels = Uint8List.fromList(List.generate(64, (i) => i * 3));
      final bitmap = decodeNativeBitmap(
        nativeTestBitmap(4, 4, (p) => p.setAll(0, pixels)),
      );
      expect([bitmap.width, bitmap.height], [4, 4]);
      expect(bitmap.pixels, pixels);
    });
    for (final mutation in <String, void Function(ByteData)>{
      'signature': (d) => d.setUint16(0, 0x4d43, Endian.little),
      'file size': (d) => d.setUint32(2, 1, Endian.little),
      'reserved field': (d) => d.setUint32(6, 1, Endian.little),
      'pixel offset': (d) => d.setUint32(10, 58, Endian.little),
      'info header size': (d) => d.setUint32(14, 108, Endian.little),
      'width': (d) => d.setInt32(18, 5, Endian.little),
      'bottom-up row order': (d) => d.setInt32(22, 4, Endian.little),
      'plane count': (d) => d.setUint16(26, 2, Endian.little),
      'bit depth': (d) => d.setUint16(28, 24, Endian.little),
      'compression': (d) => d.setUint32(30, 3, Endian.little),
      'image size': (d) => d.setUint32(34, 0, Endian.little),
      'resolution': (d) => d.setUint32(38, 2835, Endian.little),
      'palette size': (d) => d.setUint32(46, 256, Endian.little),
    }.entries) {
      test('refuses a different ${mutation.key}', () {
        final bytes = nativeTestBitmap(4, 4);
        mutation.value(ByteData.sublistView(bytes));
        expect(() => decodeNativeBitmap(bytes), throwsStateError);
      });
    }
    test('refuses truncated, extended and headerless input', () {
      final bytes = nativeTestBitmap(4, 4);
      expect(
        () => decodeNativeBitmap(bytes.sublist(0, bytes.length - 1)),
        throwsStateError,
      );
      expect(() => decodeNativeBitmap([...bytes, 0]), throwsStateError);
      expect(() => decodeNativeBitmap(Uint8List(20)), throwsStateError);
    });
  });

  group('admitted screen-baseline profile', () {
    Map<String, Object?> entry({
      String scenario = 'routing',
      Object cycle = 1,
      String step = 'open',
      String path = 'routing/c1-open.bmp',
      String? sha,
      Object tolerance = 0.02,
      List<Object?> masks = const [],
    }) => {
      'scenarioId': scenario,
      'cycle': cycle,
      'step': step,
      'path': path,
      'sha256': sha ?? 'a' * 64,
      'tolerance': tolerance,
      'masks': masks,
    };
    Map<String, Object?> profile(List<Object?> entries, {Object? root}) => {
      'display': {'width': 1920, 'height': 1080},
      'screenBaselines': {
        'root': root ?? r'D:\QA\baselines',
        'entries': entries,
      },
    };
    Map<String, Object?> mask(int x, int y, int w, int h) => {
      'x': x,
      'y': y,
      'width': w,
      'height': h,
    };
    List<Map<String, Object?>> distinct(int count) => [
      for (var i = 0; i < count; i++)
        entry(
          scenario: 'ten-cycles',
          cycle: i % 10 + 1,
          step: ['prepare', ...nativeDriverActions][i ~/ 10],
          path: 'ten/$i.bmp',
        ),
    ];

    test('entries, masks and tolerance are admitted per key', () {
      final baselines = NativeScreenBaselines.parse(
        profile([
          entry(masks: [mask(0, 0, 320, 40)]),
          entry(cycle: 2, path: 'routing/c2-open.bmp', tolerance: 0),
          entry(step: 'prepare', path: 'routing/c1-prepare.bmp'),
        ]),
      );
      final first = baselines.entryFor('routing', 1, 'open')!;
      expect(
        [first.path, first.digest, first.tolerance],
        ['routing/c1-open.bmp', 'a' * 64, 0.02],
      );
      expect(first.masks.single.width, 320);
      expect(baselines.entryFor('routing', 2, 'open')!.masks, isEmpty);
      expect(baselines.entryFor('routing', 1, 'prepare'), isNotNull);
      expect(baselines.entryFor('routing', 1, 'reopen'), isNull);
      expect(
        [baselines.root, baselines.width, baselines.height],
        [r'D:\QA\baselines', 1920, 1080],
      );
      expect(distinct(256), hasLength(256));
      NativeScreenBaselines.parse(profile(distinct(256)));
      NativeScreenBaselines.parse(
        profile([entry(masks: List.filled(64, mask(0, 0, 1, 1)))]),
      );
    });

    for (final refusal in <String, Map<String, Object?> Function()>{
      'a relative root': () => profile([entry()], root: 'baselines'),
      'a UNC root': () => profile([entry()], root: r'\\server\share'),
      'an unknown block field': () => profile([])
        ..['screenBaselines'] = {
          'root': r'D:\QA\baselines',
          'entries': <Object?>[],
          'note': 1,
        },
      'a null block': () => {
        'display': {'width': 1920, 'height': 1080},
        'screenBaselines': null,
      },
      'a display outside the admitted range': () =>
          profile([entry()])..['display'] = {'width': 320, 'height': 1080},
      'an unknown entry field': () => profile([
        {...entry(), 'note': 1},
      ]),
      'a tolerance above one': () => profile([entry(tolerance: 1.5)]),
      'a negative tolerance': () => profile([entry(tolerance: -0.1)]),
      'a tolerance as text': () => profile([entry(tolerance: '0.1')]),
      'a mask leaving the display': () => profile([
        entry(masks: [mask(1900, 0, 40, 40)]),
      ]),
      'an empty mask': () => profile([
        entry(masks: [mask(0, 0, 0, 40)]),
      ]),
      'a mask with an unknown field': () => profile([
        entry(
          masks: [
            {...mask(0, 0, 1, 1), 'note': 1},
          ],
        ),
      ]),
      'a duplicate key': () =>
          profile([entry(), entry(path: 'routing/other.bmp')]),
      'an unknown scenario': () => profile([entry(scenario: 'launch')]),
      'a cycle beyond the contract': () => profile([entry(cycle: 3)]),
      'a fractional cycle': () => profile([entry(cycle: 1.0)]),
      'a step outside the vocabulary': () => profile([entry(step: 'hide')]),
      'a non-BMP path': () => profile([entry(path: 'routing/c1-open.png')]),
      'an escaping path': () => profile([entry(path: '../c1-open.bmp')]),
      'a malformed digest': () => profile([entry(sha: 'abc')]),
      'an uppercase digest': () => profile([entry(sha: 'A' * 64)]),
      'more than 256 entries': () => profile(distinct(257)),
      'more than 64 masks': () =>
          profile([entry(masks: List.filled(65, mask(0, 0, 1, 1)))]),
    }.entries) {
      test('refuses ${refusal.key}', () {
        expect(
          () => NativeScreenBaselines.parse(refusal.value()),
          throwsStateError,
        );
      });
    }
  });

  test('verification refuses a capture of another size', () {
    final digest = NativeScreenFixture.sceneDigest;
    final baselines = NativeScreenBaselines.parse({
      'display': {'width': 640, 'height': 480},
      'screenBaselines': {
        'root': r'D:\QA\baselines',
        'entries': [
          {
            'scenarioId': 'routing',
            'cycle': 1,
            'step': 'open',
            'path': 'open.bmp',
            'sha256': digest,
            'tolerance': 0.02,
            'masks': <Object?>[],
          },
        ],
      },
    });
    final wide = nativeTestBitmap(800, 600);
    expect(
      () => verifyNativeScreenBaseline(
        NativeScreenBaselineInputs(
          baselines,
          readCapture: (_) => wide,
          readBaseline: (_) => NativeScreenFixture.scene,
        ),
        baselines.entryFor('routing', 1, 'open')!,
        {
          'path': 'screen-0002.bmp',
          'sha256': sha256.convert(wide).toString(),
          'baseline': {
            'path': 'open.bmp',
            'sha256': digest,
            'mismatchFraction': 0.0,
            'withinTolerance': true,
          },
        },
      ),
      throwsA(
        isA<StateError>().having(
          (e) => e.message,
          'message',
          contains('screen-0002.bmp is 800x600, not the admitted 640x480'),
        ),
      ),
    );
  });
}
