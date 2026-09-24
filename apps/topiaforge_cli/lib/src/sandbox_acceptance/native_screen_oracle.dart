import 'native_screen_bitmap.dart';

final class NativeScreenMeasurement {
  const NativeScreenMeasurement(this.width, this.height, this.colors);
  final int width, height, colors;
}

/// Validates and measures the broker's exact top-down 32-bit BMP format at an
/// admitted display size.
NativeScreenMeasurement measureNativeScreen(List<int> input) {
  final bitmap = decodeNativeBitmap(input);
  if (bitmap.width < nativeScreenMinimumWidth ||
      bitmap.height < nativeScreenMinimumHeight) {
    throw StateError('Native BMP layout or dimensions differ.');
  }
  final pixels = bitmap.pixels;
  final count = bitmap.width * bitmap.height;
  final colors = <int>{};
  final step = (count ~/ 16384).clamp(1, count) * 4;
  for (var at = 0; at < pixels.length; at += step) {
    colors.add(pixels[at] | pixels[at + 1] << 8 | pixels[at + 2] << 16);
  }
  return NativeScreenMeasurement(bitmap.width, bitmap.height, colors.length);
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
