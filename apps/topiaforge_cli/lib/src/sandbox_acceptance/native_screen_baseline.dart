/// Reviewed screen baselines from the admitted device profile, bounded exactly
/// as the broker's `ScreenBaselineSet.Read` bounds them, and the verifier's
/// independent recomputation of every broker baseline record (spec section 2).
/// The broker's `mismatchFraction`/`withinTolerance` are claims to check,
/// never results to trust.
library;

import 'package:crypto/crypto.dart';
import 'native_annex.dart';
import 'native_driver_vocabulary.dart';
import 'native_screen_bitmap.dart';
import 'native_transcript_actions.dart';
import 'sandbox_json.dart';

const nativeScreenBaselineEntryLimit = 256, nativeScreenBaselineMaskLimit = 64;

final class NativeScreenBaselineEntry {
  const NativeScreenBaselineEntry(
    this.path,
    this.digest,
    this.tolerance,
    this.masks,
  );

  /// Safe relative `.bmp` path beneath [NativeScreenBaselines.root].
  final String path;

  /// Lowercase SHA-256 of the reviewed baseline bytes.
  final String digest;
  final num tolerance;
  final List<NativeScreenMask> masks;
}

final class NativeScreenBaselines {
  NativeScreenBaselines._(this.root, this.width, this.height, this._entries);

  /// No `screenBaselines` block: every visual check stays unavailable.
  NativeScreenBaselines.none() : this._('', 0, 0, const {});

  /// Absolute directory of the reviewed baseline files.
  final String root;

  /// The admitted display; captures and baselines must match it exactly.
  final int width, height;
  final Map<String, NativeScreenBaselineEntry> _entries;

  NativeScreenBaselineEntry? entryFor(
    String scenarioId,
    int cycle,
    String step,
  ) => _entries['$scenarioId|$cycle|$step'];

  static NativeScreenBaselines parse(Map<String, Object?> deviceProfile) {
    if (!deviceProfile.containsKey('screenBaselines')) {
      return NativeScreenBaselines.none();
    }
    final display = sandboxObject(deviceProfile['display'], 'device display');
    final width = nativeInteger(
      display['width'],
      nativeScreenMinimumWidth,
      nativeScreenMaximumWidth,
    );
    final height = nativeInteger(
      display['height'],
      nativeScreenMinimumHeight,
      nativeScreenMaximumHeight,
    );
    final value = sandboxObject(
      deviceProfile['screenBaselines'],
      'screen baselines',
    );
    sandboxFields(value, {'root', 'entries'}, 'screen baselines');
    final entries = <String, NativeScreenBaselineEntry>{};
    for (final row in nativeRows(
      value['entries'],
      nativeScreenBaselineEntryLimit,
    )) {
      sandboxFields(row, {
        'scenarioId',
        'cycle',
        'step',
        'path',
        'sha256',
        'tolerance',
        'masks',
      }, 'screen baseline entry');
      final scenario = row['scenarioId'];
      final cycles = nativeCycleCounts[scenario];
      if (cycles == null) {
        throw StateError('Unknown native screen baseline scenario.');
      }
      final cycle = nativeInteger(row['cycle'], 1, cycles);
      final step = row['step'];
      if (step != 'prepare' && !nativeDriverActions.contains(step)) {
        throw StateError('Unknown native screen baseline step.');
      }
      final path = nativeRelativePath(row['path']);
      if (!path.endsWith('.bmp')) {
        throw StateError('Native screen baselines must be BMP files.');
      }
      final tolerance = row['tolerance'];
      if (tolerance is! num ||
          !tolerance.isFinite ||
          tolerance < 0 ||
          tolerance > 1) {
        throw StateError('Native screen baseline tolerance must be in 0..1.');
      }
      final entry = NativeScreenBaselineEntry(
        path,
        nativeHex(row['sha256'], 64),
        tolerance,
        List.unmodifiable([
          for (final mask in nativeRows(
            row['masks'],
            nativeScreenBaselineMaskLimit,
          ))
            _mask(mask, width, height),
        ]),
      );
      if (entries.putIfAbsent('$scenario|$cycle|$step', () => entry) != entry) {
        throw StateError('Duplicate native screen baseline entry.');
      }
    }
    return NativeScreenBaselines._(
      nativeWindowsPath(value['root']),
      width,
      height,
      Map.unmodifiable(entries),
    );
  }
}

NativeScreenMask _mask(Map<String, Object?> mask, int width, int height) {
  sandboxFields(mask, {'x', 'y', 'width', 'height'}, 'screen baseline mask');
  final rect = NativeScreenMask(
    nativeInteger(mask['x'], 0, width - 1),
    nativeInteger(mask['y'], 0, height - 1),
    nativeInteger(mask['width'], 1, width),
    nativeInteger(mask['height'], 1, height),
  );
  if (rect.x + rect.width > width || rect.y + rect.height > height) {
    throw StateError(
      'Native screen baseline mask leaves the admitted display.',
    );
  }
  return rect;
}

/// Admitted baselines plus byte access for their independent recomputation.
/// File access stays with the caller: [readCapture] resolves a retained
/// capture beneath the run root and [readBaseline] a reviewed baseline beneath
/// [NativeScreenBaselines.root]; either may throw for an absent file.
final class NativeScreenBaselineInputs {
  const NativeScreenBaselineInputs(
    this.baselines, {
    required this.readCapture,
    required this.readBaseline,
  });
  final NativeScreenBaselines baselines;
  final List<int> Function(String path) readCapture, readBaseline;
}

/// Recomputes one capture's comparison as the broker defines it: both files
/// bound to their digests and decoded at the admitted display, masks applied,
/// and a pixel mismatched when its largest B/G/R difference exceeds
/// [nativeScreenChannelThreshold]. The step fails unless the broker's record
/// names the admitted baseline, reports the same fraction and verdict, and the
/// recomputed fraction is within the reviewed tolerance.
void verifyNativeScreenBaseline(
  NativeScreenBaselineInputs inputs,
  NativeScreenBaselineEntry entry,
  Map<String, Object?> capture,
) {
  final claim = capture['baseline'];
  if (claim is! Map<String, Object?>) {
    throw StateError('Native screenshot lacks its reviewed baseline record.');
  }
  if (claim['path'] != entry.path || claim['sha256'] != entry.digest) {
    throw StateError(
      'Broker baseline record does not name the admitted baseline '
      '${entry.path}.',
    );
  }
  final baselines = inputs.baselines;
  final reviewed = _bitmap(
    inputs.readBaseline,
    entry.path,
    entry.digest,
    baselines,
    'Reviewed screen baseline',
  );
  final retained = _bitmap(
    inputs.readCapture,
    capture['path']! as String,
    capture['sha256']! as String,
    baselines,
    'Retained screen capture',
  );
  final fraction = nativeScreenMismatchFraction(
    retained.pixels,
    reviewed.pixels,
    baselines.width,
    baselines.height,
    entry.masks,
  );
  final within = fraction <= entry.tolerance;
  // Both sides divide the same pixel counts in IEEE doubles and the broker
  // writes shortest round-trip JSON, so an honest record is bit-identical.
  if (claim['mismatchFraction'] != fraction ||
      claim['withinTolerance'] != within) {
    throw StateError(
      'Broker baseline record for ${entry.path} differs from the recomputed '
      'comparison.',
    );
  }
  if (!within) {
    throw StateError(
      'Native screenshot exceeds the reviewed tolerance of ${entry.path}.',
    );
  }
}

NativeBitmap _bitmap(
  List<int> Function(String path) read,
  String path,
  String digest,
  NativeScreenBaselines baselines,
  String label,
) {
  final List<int> bytes;
  try {
    bytes = read(path);
  } on Object {
    throw StateError('$label $path is missing or unreadable.');
  }
  if (sha256.convert(bytes).toString() != digest) {
    throw StateError('$label $path does not match its bound SHA-256.');
  }
  final NativeBitmap bitmap;
  try {
    bitmap = decodeNativeBitmap(bytes);
  } on StateError {
    throw StateError('$label $path is not in the broker BMP layout.');
  }
  if (bitmap.width != baselines.width || bitmap.height != baselines.height) {
    throw StateError(
      '$label $path is ${bitmap.width}x${bitmap.height}, not the admitted '
      '${baselines.width}x${baselines.height} display.',
    );
  }
  return bitmap;
}
