import 'dart:typed_data';
import 'package:crypto/crypto.dart';
import 'package:test/test.dart';
import 'package:topiaforge/src/sandbox_acceptance/native_annex_verifier.dart';
import 'sandbox_native_screen_fixture.dart';
import 'sandbox_native_transcript_fixture.dart';

/// The verifier recomputes every reviewed-baseline comparison from the
/// retained capture and the admitted baseline bytes; the broker's record is
/// only a claim that must agree. Each case faults routing cycle 1's first
/// capture (step `open`) with synthetic pixels.
void main() {
  const open = 'routing|1|open', baseline = 'routing/c1-open.bmp';
  const width = NativeScreenFixture.width, height = NativeScreenFixture.height;

  /// Applies [edit] to the blue channel of a top-left image rectangle.
  void Function(Uint8List) region(int w, int h, int Function(int) edit) =>
      (pixels) {
        for (var y = 0; y < h; y++) {
          for (var x = 0; x < w; x++) {
            final at = (y * width + x) * 4;
            pixels[at] = edit(pixels[at]);
          }
        }
      };
  int flip(int value) => value ^ 0x80; // a 128-level difference
  int Function(int) shift(int delta) =>
      (value) => value < 128 ? value + delta : value - delta;
  // 640 x 96 is exactly a fifth of the capture, above the 0.02 tolerance.
  final fifth = region(width, 96, flip);
  NativeScenarioTranscript routing() => NativeScenarioTranscript('routing');
  void claim(NativeScenarioTranscript f, double fraction, bool within) =>
      f.editEvent('capture', 0, (data) {
        (data['baseline']! as Map)
          ..['mismatchFraction'] = fraction
          ..['withinTolerance'] = within;
      });
  void expectFailed(NativeScenarioTranscript f, String reason) {
    final result = f.result();
    expect(result.status, SandboxNativeStatus.failed);
    expect(result.reason, contains(reason));
  }

  void expectPassed(NativeScenarioTranscript f) {
    final result = f.result();
    expect(result.status, SandboxNativeStatus.passed, reason: result.reason);
  }

  test('matching pixels with an honest record pass', () {
    expectPassed(routing());
  });

  group('forged or disagreeing broker records fail', () {
    test('an in-tolerance record over differing pixels', () {
      expectFailed(
        routing()..screens.captureEdits[0] = fifth,
        'differs from the recomputed comparison',
      );
    });
    test('the true fraction with a forged in-tolerance verdict', () {
      final f = routing()..screens.captureEdits[0] = fifth;
      claim(f, 0.2, true);
      expectFailed(f, 'differs from the recomputed comparison');
    });
    test('a record naming another baseline digest', () {
      expectFailed(
        routing()..editEvent('capture', 0, (data) {
          (data['baseline']! as Map)['sha256'] = 'e' * 64;
        }),
        'does not name the admitted baseline $baseline',
      );
    });
    test('a record naming another baseline file', () {
      expectFailed(
        routing()..editEvent('capture', 0, (data) {
          (data['baseline']! as Map)['path'] = 'routing/c2-open.bmp';
        }),
        'does not name the admitted baseline $baseline',
      );
    });
  });

  test('an honest record beyond tolerance fails on tolerance', () {
    final f = routing()..screens.captureEdits[0] = fifth;
    claim(f, 0.2, false);
    expectFailed(f, 'exceeds the reviewed tolerance of $baseline');
  });

  group('genuine agreement within tolerance passes', () {
    test('a 1% difference under the 2% tolerance', () {
      final f = routing()..screens.captureEdits[0] = region(64, 48, flip);
      claim(f, 64 * 48 / (width * height), true);
      expectPassed(f);
    });
    test('differences confined to a reviewed mask', () {
      expectPassed(
        routing()
          ..screens.captureEdits[0] = fifth
          ..screens.entryEdits[open] = (entry) => entry['masks'] = [
            {'x': 0, 'y': 0, 'width': width, 'height': 96},
          ],
      );
    });
    test('channel differences of exactly the threshold', () {
      expectPassed(
        routing()..screens.captureEdits[0] = region(width, height, shift(8)),
      );
    });
    test('alpha-only differences', () {
      expectPassed(
        routing()
          ..screens.captureEdits[0] = (pixels) {
            for (var at = 3; at < pixels.length; at += 4) {
              pixels[at] = 0;
            }
          },
      );
    });
  });

  test('one level above the threshold is a mismatch', () {
    expectFailed(
      routing()..screens.captureEdits[0] = region(width, height, shift(9)),
      'differs from the recomputed comparison',
    );
  });

  group('stale, missing or misshapen files fail closed', () {
    test('a stale baseline file under the admitted digest', () {
      expectFailed(
        routing()
          ..screens.baselineFiles[baseline] = nativeTestBitmap(width, height),
        'Reviewed screen baseline $baseline does not match its bound SHA-256',
      );
    });
    test('a retained capture differing from its recorded digest', () {
      final f = routing();
      f.transcriptBytes;
      f.screens.captures[f.artifacts.first['path']! as String] =
          nativeTestBitmap(width, height);
      expectFailed(f, 'does not match its bound SHA-256');
    });
    test('a missing retained capture', () {
      final f = routing();
      f.transcriptBytes;
      final path = f.artifacts.first['path']! as String;
      f.screens.captures.remove(path);
      expectFailed(f, 'Retained screen capture $path is missing or unreadable');
    });
    test('a missing reviewed baseline', () {
      final f = routing();
      f.transcriptBytes;
      f.screens.baselineFiles.remove(baseline);
      expectFailed(
        f,
        'Reviewed screen baseline $baseline is missing or unreadable',
      );
    });
    test('a digest-matching baseline of another size', () {
      final other = nativeTestBitmap(
        800,
        600,
        (pixels) => paintNativeTestScene(pixels, 800),
      );
      expectFailed(
        routing()
          ..screens.baselineFiles[baseline] = other
          ..screens.entryEdits[open] = (entry) =>
              entry['sha256'] = sha256.convert(other).toString(),
        'is 800x600, not the admitted 640x480 display',
      );
    });
    test('a digest-matching baseline outside the broker layout', () {
      final bottomUp = Uint8List.fromList(NativeScreenFixture.scene);
      ByteData.sublistView(bottomUp).setInt32(22, height, Endian.little);
      expectFailed(
        routing()
          ..screens.baselineFiles[baseline] = bottomUp
          ..screens.entryEdits[open] = (entry) =>
              entry['sha256'] = sha256.convert(bottomUp).toString(),
        'is not in the broker BMP layout',
      );
    });
  });
}
