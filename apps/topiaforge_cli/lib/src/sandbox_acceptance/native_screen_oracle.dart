import 'dart:typed_data';

final class NativeScreenMeasurement {
  const NativeScreenMeasurement(this.width, this.height, this.colors);
  final int width, height, colors;
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
