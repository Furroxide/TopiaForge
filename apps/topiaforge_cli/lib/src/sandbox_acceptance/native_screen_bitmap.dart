/// The broker's only screenshot format and its reviewed-baseline arithmetic,
/// mirrored from `ScreenCapture.Capture` and `ScreenBaselineSet`
/// (`tools/TopiaForge.Acceptance.Windows/ScreenBaselines.cs`). Pure and
/// allocation-bounded; file access stays with the callers.
library;

import 'dart:typed_data';

/// BITMAPFILEHEADER (14 bytes) plus BITMAPINFOHEADER (40 bytes).
const nativeBitmapHeaderBytes = 54;

/// Admitted display bounds (device profile `display`).
const nativeScreenMinimumWidth = 640, nativeScreenMinimumHeight = 480;
const nativeScreenMaximumWidth = 7680, nativeScreenMaximumHeight = 4320;
const nativeBitmapMaximumBytes =
    nativeBitmapHeaderBytes +
    nativeScreenMaximumWidth * nativeScreenMaximumHeight * 4;

/// A pixel mismatches when its largest B, G or R difference exceeds this value
/// on the 0–255 scale; alpha is ignored (`ScreenBaselineSet.ChannelThreshold`).
const nativeScreenChannelThreshold = 8;

final class NativeBitmap {
  NativeBitmap._(this.width, this.height, this.pixels);
  final int width, height;

  /// Top-down BGRA rows, four bytes per pixel, without padding.
  final Uint8List pixels;
}

/// Decodes exactly the broker's own layout: `BM`, a file size equal to the
/// byte count, zero reserved fields, a 54-byte pixel offset, a 40-byte info
/// header, a negative (top-down) height, one plane, 32 bits per pixel, no
/// compression, an image size of width × height × 4 and zero resolution and
/// palette fields. Any other bitmap is refused rather than interpreted.
NativeBitmap decodeNativeBitmap(List<int> input) {
  if (input.length < nativeBitmapHeaderBytes + 4 ||
      input.length > nativeBitmapMaximumBytes) {
    throw StateError('Native BMP size is invalid.');
  }
  final bytes = input is Uint8List ? input : Uint8List.fromList(input);
  final data = ByteData.sublistView(bytes);
  int u16(int offset) => data.getUint16(offset, Endian.little);
  int u32(int offset) => data.getUint32(offset, Endian.little);
  final width = data.getInt32(18, Endian.little);
  final height = -data.getInt32(22, Endian.little);
  if (u16(0) != 0x4d42 ||
      u32(2) != bytes.length ||
      u32(6) != 0 ||
      u32(10) != nativeBitmapHeaderBytes ||
      u32(14) != 40 ||
      width < 1 ||
      width > nativeScreenMaximumWidth ||
      height < 1 ||
      height > nativeScreenMaximumHeight ||
      u16(26) != 1 ||
      u16(28) != 32 ||
      u32(30) != 0 ||
      u32(34) != width * height * 4 ||
      bytes.length != nativeBitmapHeaderBytes + width * height * 4 ||
      [38, 42, 46, 50].any((offset) => u32(offset) != 0)) {
    throw StateError('Native BMP layout differs from the broker format.');
  }
  return NativeBitmap._(
    width,
    height,
    Uint8List.sublistView(bytes, nativeBitmapHeaderBytes),
  );
}

/// A top-left image-pixel rectangle excluded from comparison.
final class NativeScreenMask {
  const NativeScreenMask(this.x, this.y, this.width, this.height);
  final int x, y, width, height;
}

/// `ScreenBaselineSet.MismatchFraction`: mismatched unmasked pixels divided by
/// unmasked pixels. Overlapping masks count once; a mask leaving the image or
/// masks covering every pixel are refused.
double nativeScreenMismatchFraction(
  Uint8List capture,
  Uint8List baseline,
  int width,
  int height,
  List<NativeScreenMask> masks,
) {
  if (width < 1 ||
      height < 1 ||
      capture.length != width * height * 4 ||
      baseline.length != width * height * 4) {
    throw StateError('Baseline and capture dimensions differ.');
  }
  final masked = Uint8List(width * height);
  for (final mask in masks) {
    if (mask.x < 0 ||
        mask.y < 0 ||
        mask.width < 1 ||
        mask.height < 1 ||
        mask.x + mask.width > width ||
        mask.y + mask.height > height) {
      throw StateError('Baseline mask leaves the admitted display.');
    }
    for (var row = mask.y; row < mask.y + mask.height; row++) {
      final start = row * width + mask.x;
      masked.fillRange(start, start + mask.width, 1);
    }
  }
  var unmasked = 0, mismatched = 0;
  for (var pixel = 0; pixel < masked.length; pixel++) {
    if (masked[pixel] != 0) continue;
    unmasked++;
    // The largest B/G/R difference exceeds the threshold iff any one does.
    final offset = pixel * 4;
    if ((capture[offset] - baseline[offset]).abs() >
            nativeScreenChannelThreshold ||
        (capture[offset + 1] - baseline[offset + 1]).abs() >
            nativeScreenChannelThreshold ||
        (capture[offset + 2] - baseline[offset + 2]).abs() >
            nativeScreenChannelThreshold) {
      mismatched++;
    }
  }
  if (unmasked == 0) {
    throw StateError('Baseline masks cover the entire capture.');
  }
  return mismatched / unmasked;
}
