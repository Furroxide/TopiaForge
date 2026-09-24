import 'dart:math' as math;
import 'dart:typed_data';

/// Recomputed from the retained WAV, never from broker-provided metrics.
final class NativeAudioMeasurement {
  const NativeAudioMeasurement(
    this.sampleRate,
    this.channels,
    this.bits,
    this.encoding,
    this.frames,
    this.rms,
    this.peak,
    this.cuePower,
    this.controlPower,
  );
  final int sampleRate, channels, bits, frames;
  final String encoding;
  final double rms, peak, cuePower, controlPower;
  double get milliseconds => frames * 1000 / sampleRate;
  bool get containsCue => cuePower > 2.5e-9 && cuePower > controlPower * 8;
}

/// Production's deterministic cue contract for the private graph fixture.
double get sandboxGraphCueFrequency {
  var hash = 17;
  for (final code in 'sandbox-acceptance-graph'.codeUnits) {
    hash = ((hash * 31 + code) & 0xffffffff).toSigned(32);
  }
  return 440 + (hash.remainder(180)).abs().toDouble();
}

NativeAudioMeasurement measureNativeAudio(List<int> input) {
  if (input.length < 58 || input.length > 20 * 1024 * 1024) {
    throw StateError('Native WAV size is invalid.');
  }
  final bytes = Uint8List.fromList(input);
  final data = ByteData.sublistView(bytes);
  String tag(int offset) =>
      String.fromCharCodes(bytes.sublist(offset, offset + 4));
  int u16(int offset) => data.getUint16(offset, Endian.little);
  int u32(int offset) => data.getUint32(offset, Endian.little);
  if (tag(0) != 'RIFF' || tag(8) != 'WAVE' || u32(4) != bytes.length - 8) {
    throw StateError('Native WAV identity or length differs.');
  }
  final chunks = <String, (int, int)>{};
  var offset = 12;
  while (offset < bytes.length) {
    if (offset + 8 > bytes.length) throw StateError('Truncated WAV chunk.');
    final name = tag(offset), length = u32(offset + 4);
    if (!{'fmt ', 'fact', 'data'}.contains(name) ||
        chunks.containsKey(name) ||
        offset + 8 + length > bytes.length) {
      throw StateError('Unknown, repeated or truncated WAV chunk.');
    }
    chunks[name] = (offset + 8, length);
    offset += 8 + length + (length & 1);
  }
  if (offset != bytes.length || chunks.length != 3) {
    throw StateError('Incomplete native WAV.');
  }
  final (format, formatLength) = chunks['fmt ']!;
  if (![18, 40].contains(formatLength)) {
    throw StateError('Unsupported WAV format.');
  }
  var encoding = u16(format);
  final channels = u16(format + 2), rate = u32(format + 4);
  final align = u16(format + 12), bits = u16(format + 14);
  if (encoding == 65534) {
    if (formatLength != 40 ||
        u16(format + 16) != 22 ||
        u16(format + 18) != bits ||
        !List.generate(12, (i) => bytes[format + 28 + i]).asMap().entries.every(
          (e) =>
              e.value ==
              const [0, 0, 16, 0, 128, 0, 0, 170, 0, 56, 155, 113][e.key],
        )) {
      throw StateError('Unsupported extensible WAV precision/subtype.');
    }
    encoding = u32(format + 24);
  } else if (formatLength != 18 || u16(format + 16) != 0) {
    throw StateError('Unsupported WAV extension.');
  }
  if (channels < 1 ||
      channels > 8 ||
      rate < 8000 ||
      rate > 192000 ||
      ![1, 3].contains(encoding) ||
      bits != (encoding == 3 ? 32 : 16) ||
      align != channels * bits ~/ 8 ||
      u32(format + 8) != rate * align) {
    throw StateError('Native audio format is out of bounds.');
  }
  final (start, length) = chunks['data']!;
  if (length == 0 || length % align != 0) {
    throw StateError('Incomplete audio frames.');
  }
  final frames = length ~/ align;
  if (frames > rate * 3 ||
      chunks['fact']!.$2 != 4 ||
      u32(chunks['fact']!.$1) != frames) {
    throw StateError('WAV frame count differs.');
  }
  final mono = Float64List(frames);
  var squares = 0.0, peak = 0.0;
  for (var frame = 0; frame < frames; frame++) {
    for (var channel = 0; channel < channels; channel++) {
      final at = start + frame * align + channel * bits ~/ 8;
      final sample = encoding == 3
          ? data.getFloat32(at, Endian.little)
          : data.getInt16(at, Endian.little) / 32768;
      if (!sample.isFinite || sample.abs() > 8) {
        throw StateError('Invalid WAV sample.');
      }
      squares += sample * sample;
      peak = math.max(peak, sample.abs());
      // A render channel can legitimately be silent; choose the loudest channel
      // per sample for cue presence, while RMS covers every actual channel.
      if (sample.abs() > mono[frame].abs()) mono[frame] = sample;
    }
  }
  var cue = 0.0, controls = 0.0;
  final window = math.min(frames, (rate * 0.08).round());
  for (var begin = 0; begin + window <= frames; begin += window) {
    final target = _power(mono, begin, window, rate, sandboxGraphCueFrequency);
    final control = math.max(
      _power(mono, begin, window, rate, sandboxGraphCueFrequency - 50),
      _power(mono, begin, window, rate, sandboxGraphCueFrequency + 50),
    );
    if (target > cue) {
      cue = target;
      controls = control;
    }
  }
  return NativeAudioMeasurement(
    rate,
    channels,
    bits,
    encoding == 3 ? 'ieee-float' : 'pcm',
    frames,
    math.sqrt(squares / (frames * channels)),
    peak,
    cue,
    controls,
  );
}

double _power(
  Float64List samples,
  int start,
  int count,
  int rate,
  double frequency,
) {
  final coefficient = 2 * math.cos(2 * math.pi * frequency / rate);
  var first = 0.0, second = 0.0;
  for (var i = 0; i < count; i++) {
    final next = samples[start + i] + coefficient * first - second;
    second = first;
    first = next;
  }
  return math.max(
    0.0,
    (first * first + second * second - coefficient * first * second) /
        (count * count),
  );
}

void verifyNativeAudioClaim(
  Map<String, Object?> claim,
  NativeAudioMeasurement actual,
) {
  for (final entry in <String, Object>{
    'sampleRate': actual.sampleRate,
    'channels': actual.channels,
    'bitsPerSample': actual.bits,
    'encoding': actual.encoding,
    'frames': actual.frames,
  }.entries) {
    if (claim[entry.key] != entry.value) {
      throw StateError('Audio claim differs from actual WAV ${entry.key}.');
    }
  }
  for (final entry in <String, double>{
    'rms': actual.rms,
    'peak': actual.peak,
    'capturedMilliseconds': actual.milliseconds,
  }.entries) {
    final claimed = claim[entry.key];
    if (claimed is! num ||
        !claimed.isFinite ||
        (claimed - entry.value).abs() > 1e-8 * math.max(1, entry.value)) {
      throw StateError(
        'Audio measurement differs from actual WAV ${entry.key}.',
      );
    }
  }
}

void validateNativeAudioMetadata(Map<String, Object?> value) {
  int integer(String key, int minimum, int maximum) {
    final result = value[key];
    if (result is! int || result < minimum || result > maximum) {
      throw StateError('Invalid native audio $key.');
    }
    return result;
  }

  final frames = integer('frames', 1, 192000 * 3);
  integer('discontinuities', 0, frames);
  integer('timestampErrors', 0, frames);
  integer('silentFrames', 0, frames);
  integer('requestedMilliseconds', 100, 3000);
  if (value['initialDiscontinuity'] is! bool ||
      value['initialDiscontinuity'] == true && value['discontinuities'] == 0) {
    throw StateError('Invalid native audio discontinuity state.');
  }
  final endpoint = value['endpointId'];
  if (endpoint is! String ||
      endpoint.isEmpty ||
      endpoint.length > 1024 ||
      endpoint.codeUnits.any((c) => c < 32)) {
    throw StateError('Invalid native audio endpoint.');
  }
  DateTime timestamp(String key) {
    final raw = value[key];
    if (raw is! String || raw.length > 40 || !raw.endsWith('Z')) {
      throw StateError('Invalid native audio timestamp.');
    }
    final parsed = DateTime.tryParse(raw);
    if (parsed == null || !parsed.isUtc) {
      throw StateError('Invalid native audio timestamp.');
    }
    return parsed;
  }

  final duration = timestamp(
    'completedUtc',
  ).difference(timestamp('startedUtc'));
  if (duration.isNegative || duration > const Duration(seconds: 10)) {
    throw StateError('Native audio timestamp order or duration is invalid.');
  }
}
