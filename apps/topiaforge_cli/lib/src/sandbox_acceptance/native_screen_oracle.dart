import 'dart:typed_data';

import 'native_annex.dart';
import 'sandbox_json.dart';

final class NativeScreenMeasurement {
  const NativeScreenMeasurement(this.width, this.height, this.colors);
  final int width, height, colors;
}

/// Builds the set of `scenarioId|cycle|step` keys with a reviewed device-profile
/// baseline entry (spec section 2). A missing `screenBaselines` block yields an
/// empty set, which makes every visual check `unavailable` (never passed).
Set<String> nativeScreenBaselineSteps(Map<String, Object?> deviceProfile) {
  final baselines = deviceProfile['screenBaselines'];
  if (baselines == null) return const {};
  final root = sandboxObject(baselines, 'screen baselines');
  sandboxFields(root, {'root', 'entries'}, 'screen baselines');
  sandboxText(root['root'], 'screen baseline root', maximum: 1024);
  final keys = <String>{};
  for (final entry in nativeRows(root['entries'], 4096)) {
    sandboxFields(entry, {
      'scenarioId',
      'cycle',
      'step',
      'path',
      'sha256',
      'tolerance',
      'masks',
    }, 'screen baseline entry');
    final scenario = sandboxText(entry['scenarioId'], 'baseline scenario');
    final cycle = nativeInteger(entry['cycle'], 1, 10);
    final step = sandboxText(entry['step'], 'baseline step', maximum: 128);
    nativeRelativePath(entry['path']);
    nativeHex(entry['sha256'], 64);
    final tolerance = entry['tolerance'];
    if (tolerance is! num ||
        !tolerance.isFinite ||
        tolerance < 0 ||
        tolerance > 1 ||
        entry['masks'] is! List ||
        (entry['masks']! as List).length > 256) {
      throw StateError('Invalid native screen baseline entry.');
    }
    if (!keys.add('$scenario|$cycle|$step')) {
      throw StateError('Duplicate native screen baseline entry.');
    }
  }
  return keys;
}

/// Validates and measures the broker's exact top-down 32-bit BMP format.
NativeScreenMeasurement measureNativeScreen(List<int> input) {
  if (input.length < 58 || input.length > 128 * 1024 * 1024) {
    throw StateError('Native screen size is invalid.');
  }
  final bytes = input is Uint8List ? input : Uint8List.fromList(input);
  final data = ByteData.sublistView(bytes);
  int u16(int offset) => data.getUint16(offset, Endian.little);
  int u32(int offset) => data.getUint32(offset, Endian.little);
  final width = data.getInt32(18, Endian.little);
  final negativeHeight = data.getInt32(22, Endian.little);
  final height = -negativeHeight;
  if (u16(0) != 0x4d42 ||
      u32(2) != bytes.length ||
      u32(6) != 0 ||
      u32(10) != 54 ||
      u32(14) != 40 ||
      u16(26) != 1 ||
      u16(28) != 32 ||
      u32(30) != 0 ||
      width < 640 ||
      width > 7680 ||
      height < 480 ||
      height > 4320 ||
      u32(34) != width * height * 4 ||
      bytes.length != 54 + width * height * 4 ||
      [38, 42, 46, 50].any((offset) => u32(offset) != 0)) {
    throw StateError('Native BMP layout or dimensions differ.');
  }
  final colors = <int>{};
  final step = (width * height ~/ 16384).clamp(1, width * height) * 4;
  for (var at = 54; at < bytes.length; at += step) {
    colors.add(bytes[at] | bytes[at + 1] << 8 | bytes[at + 2] << 16);
  }
  return NativeScreenMeasurement(width, height, colors.length);
}

void verifyNativeScreenClaim(
  Map<String, Object?> claim,
  NativeScreenMeasurement actual,
) {
  if (claim['width'] != actual.width ||
      claim['height'] != actual.height ||
      claim['distinctSampleColors'] != actual.colors) {
    throw StateError('Screen measurements differ from retained BMP bytes.');
  }
  if (actual.colors < 8) {
    throw StateError('Retained screen is blank or unusable.');
  }
}
