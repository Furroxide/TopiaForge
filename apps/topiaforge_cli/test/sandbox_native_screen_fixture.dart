import 'dart:io';
import 'dart:typed_data';
import 'package:crypto/crypto.dart';
import 'package:topiaforge/src/sandbox_acceptance/native_screen_baseline.dart';
import 'package:topiaforge/src/sandbox_acceptance/native_screen_oracle.dart';

/// Frames BGRA pixels in the broker's top-down 32-bit BMP layout, the test
/// counterpart of `ScreenBaselineSet.EncodeBitmap`. [paint] edits the
/// zero-filled pixels first.
Uint8List nativeTestBitmap(
  int width,
  int height, [
  void Function(Uint8List pixels)? paint,
]) {
  final bytes = Uint8List(54 + width * height * 4);
  final data = ByteData.sublistView(bytes);
  data.setUint16(0, 0x4d42, Endian.little);
  data.setUint32(2, bytes.length, Endian.little);
  data.setUint32(10, 54, Endian.little);
  data.setUint32(14, 40, Endian.little);
  data.setInt32(18, width, Endian.little);
  data.setInt32(22, -height, Endian.little);
  data.setUint16(26, 1, Endian.little);
  data.setUint16(28, 32, Endian.little);
  data.setUint32(34, width * height * 4, Endian.little);
  paint?.call(Uint8List.sublistView(bytes, 54));
  return bytes;
}

/// A varied opaque synthetic scene: each channel follows the pixel position.
void paintNativeTestScene(Uint8List pixels, int width) {
  for (var pixel = 0; pixel < pixels.length ~/ 4; pixel++) {
    final x = pixel % width, y = pixel ~/ width, at = pixel * 4;
    pixels[at] = x & 0xff;
    pixels[at + 1] = y & 0xff;
    pixels[at + 2] = (x + 2 * y) & 0xff;
    pixels[at + 3] = 0xff;
  }
}

/// Synthetic reviewed baselines and retained captures for transcript tests,
/// never real screenshots. Each screen step gets an admitted profile entry
/// whose baseline bytes equal the capture, so the broker's honest record is
/// `0.0`/`true`, until a test edits a capture, a baseline file, an entry or
/// the recorded claim.
final class NativeScreenFixture {
  static const width = 640, height = 480;
  static final Uint8List scene = nativeTestBitmap(
    width,
    height,
    (pixels) => paintNativeTestScene(pixels, width),
  );
  static final String sceneDigest = sha256.convert(scene).toString();
  static final NativeScreenMeasurement _sceneMeasurement = measureNativeScreen(
    scene,
  );

  /// Pixel edits for the transcript's [ordinal]-th capture, applied before
  /// its digest and measurement are recorded.
  final Map<int, void Function(Uint8List pixels)> captureEdits = {};

  /// Edits of a newly admitted `scenario|cycle|step` profile entry.
  final Map<String, void Function(Map<String, Object?> entry)> entryEdits = {};

  /// Retained capture bytes by run-relative path; a removed path is absent.
  final Map<String, List<int>> captures = {};

  /// Reviewed baseline bytes by root-relative path; preset files are kept.
  final Map<String, List<int>> baselineFiles = {};
  final Map<String, NativeScreenMeasurement> measurements = {};

  /// Admitted device-profile entries keyed `scenario|cycle|step`.
  final Map<String, Map<String, Object?>> entries = {};
  int _ordinal = 0;

  /// Records one retained capture and returns the broker's `capture` data.
  Map<String, Object?> capture(
    String scenario,
    int cycle,
    String step,
    String path,
  ) {
    final edit = captureEdits[_ordinal++];
    final bytes = edit == null
        ? scene
        : nativeTestBitmap(width, height, (pixels) {
            pixels.setAll(0, Uint8List.sublistView(scene, 54));
            edit(pixels);
          });
    captures[path] = bytes;
    final measured = measurements[path] = edit == null
        ? _sceneMeasurement
        : measureNativeScreen(bytes);
    final key = '$scenario|$cycle|$step';
    final entry = entries.putIfAbsent(key, () {
      final created = <String, Object?>{
        'scenarioId': scenario,
        'cycle': cycle,
        'step': step,
        'path': '$scenario/c$cycle-$step.bmp',
        'sha256': sceneDigest,
        'tolerance': 0.02,
        'masks': <Object?>[],
      };
      entryEdits[key]?.call(created);
      return created;
    });
    baselineFiles.putIfAbsent(entry['path']! as String, () => scene);
    return {
      'path': path,
      'width': width,
      'height': height,
      'distinctSampleColors': measured.colors,
      'sha256': edit == null ? sceneDigest : sha256.convert(bytes).toString(),
      'length': bytes.length,
      'baseline': {
        'path': entry['path'],
        'sha256': entry['sha256'],
        'mismatchFraction': 0.0,
        'withinTolerance': true,
      },
    };
  }

  /// The admitted profile, parsed as the verifier parses it, with readers
  /// that report an absent synthetic file like a missing one on disk.
  NativeScreenBaselineInputs inputs() => NativeScreenBaselineInputs(
    NativeScreenBaselines.parse({
      'display': {
        'width': width,
        'height': height,
        'dpi': 96,
        'deviceName': r'\\.\DISPLAY1',
      },
      'screenBaselines': {
        'root': r'D:\QA\baselines',
        'entries': entries.values.toList(),
      },
    }),
    readCapture: (path) =>
        captures[path] ??
        (throw FileSystemException('Synthetic capture is absent.', path)),
    readBaseline: (path) =>
        baselineFiles[path] ??
        (throw FileSystemException('Synthetic baseline is absent.', path)),
  );
}
