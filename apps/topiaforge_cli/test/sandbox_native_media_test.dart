import 'dart:math' as math;
import 'dart:typed_data';
import 'package:test/test.dart';
import 'package:topiaforge/src/sandbox_acceptance/native_audio_oracle.dart';
import 'package:topiaforge/src/sandbox_acceptance/native_screen_oracle.dart';

void main() {
  test('audio packet metadata requires typed and bounded observations', () {
    final valid = <String, Object?>{
      'frames': 8000,
      'discontinuities': 1,
      'initialDiscontinuity': true,
      'timestampErrors': 0,
      'silentFrames': 0,
      'requestedMilliseconds': 1000,
      'endpointId': 'synthetic-render-endpoint',
      'startedUtc': '2026-09-09T12:00:00.000Z',
      'completedUtc': '2026-09-09T12:00:01.000Z',
    };
    validateNativeAudioMetadata(valid);
    for (final entry in <String, Object?>{
      'frames': -1,
      'discontinuities': -1,
      'initialDiscontinuity': 'true',
      'timestampErrors': 0.5,
      'silentFrames': 8001,
      'requestedMilliseconds': 10000,
      'endpointId': '',
      'startedUtc': '2026-09-09T12:00:02Z',
      'completedUtc': '2026-09-09T12:00:11Z',
    }.entries) {
      expect(
        () => validateNativeAudioMetadata({...valid, entry.key: entry.value}),
        throwsStateError,
        reason: entry.key,
      );
    }
    expect(
      () => validateNativeAudioMetadata({...valid, 'discontinuities': 0}),
      throwsStateError,
    );
  });

  test('actual PCM and float render bytes identify the fixture cue', () {
    expect(sandboxGraphCueFrequency, 599);
    for (final floating in [false, true]) {
      final measurement = measureNativeAudio(wave(599, floating: floating));
      expect(measurement.containsCue, isTrue);
      expect(measurement.frames, 8000);
      expect(measurement.channels, 1);
      expect(measurement.rms, closeTo(0.1 / math.sqrt(2), 0.0001));
      expect(measurement.peak, closeTo(0.1, 0.0001));
      expect(measurement.milliseconds, 1000);
    }
  });
  test('silence and a different audible tone are not the fixture cue', () {
    expect(measureNativeAudio(wave(0)).containsCue, isFalse);
    expect(measureNativeAudio(wave(1000)).containsCue, isFalse);
  });
  test('cue disappearance is observable in retained samples', () {
    final playing = measureNativeAudio(wave(599));
    final stopped = measureNativeAudio(wave(0));
    expect(stopped.cuePower, lessThan(playing.cuePower / 8));
    expect(
      measureNativeAudio(wave(599)).cuePower,
      greaterThan(playing.cuePower / 8),
    );
  });
  test('a forged RMS or format claim cannot pass a byte replay', () {
    final measured = measureNativeAudio(wave(599));
    final claim = <String, Object?>{
      'sampleRate': 8000,
      'channels': 1,
      'bitsPerSample': 16,
      'encoding': 'pcm',
      'frames': 8000,
      'rms': measured.rms,
      'peak': measured.peak,
      'capturedMilliseconds': 1000,
    };
    verifyNativeAudioClaim(claim, measured);
    for (final entry in <String, Object?>{
      'rms': 0,
      'peak': 1,
      'frames': 7999,
      'encoding': 'ieee-float',
      'sampleRate': 48000,
      'capturedMilliseconds': 900,
    }.entries) {
      expect(
        () => verifyNativeAudioClaim({
          ...claim,
          entry.key: entry.value,
        }, measured),
        throwsStateError,
      );
    }
  });
  final audioMutations = <String, void Function(ByteData)>{
    'RIFF length': (d) => d.setUint32(4, 1, Endian.little),
    'unknown format': (d) => d.setUint16(20, 6, Endian.little),
    'channel bound': (d) => d.setUint16(22, 16, Endian.little),
    'rate bound': (d) => d.setUint32(24, 4000, Endian.little),
    'average rate': (d) => d.setUint32(28, 1, Endian.little),
    'wrong frame count': (d) => d.setUint32(46, 3, Endian.little),
    'data size overflow': (d) => d.setUint32(54, 0x7fffffff, Endian.little),
    'NaN sample': (d) => d.setFloat32(58, double.nan, Endian.little),
  };
  for (final mutation in audioMutations.entries) {
    test('rejects audio ${mutation.key}', () {
      final bytes = wave(599, floating: true);
      mutation.value(ByteData.sublistView(bytes));
      expect(() => measureNativeAudio(bytes), throwsStateError);
    });
  }
  test('truncation and unknown chunks do not become silent audio', () {
    final bytes = wave(599);
    expect(
      () => measureNativeAudio(bytes.sublist(0, bytes.length - 1)),
      throwsStateError,
    );
    bytes.setRange(38, 42, 'junk'.codeUnits);
    expect(() => measureNativeAudio(bytes), throwsStateError);
  });
  test('screen metrics come from actual pixels and ignore alpha', () {
    final bytes = screen();
    final measured = measureNativeScreen(bytes);
    expect(measured.width, 640);
    expect(measured.height, 480);
    expect(measured.colors, greaterThanOrEqualTo(8));
    verifyNativeScreenClaim({
      'width': 640,
      'height': 480,
      'distinctSampleColors': measured.colors,
    }, measured);
    expect(
      () => verifyNativeScreenClaim({
        'width': 640,
        'height': 480,
        'distinctSampleColors': 1,
      }, measured),
      throwsStateError,
    );
    for (var at = 57; at < bytes.length; at += 4) {
      bytes[at] = at % 256;
    }
    expect(measureNativeScreen(bytes).colors, measured.colors);
  });
  test('blank pixels cannot be made usable by claimed color counts', () {
    final bytes = screen()..fillRange(54, 54 + 640 * 480 * 4, 0);
    expect(measureNativeScreen(bytes).colors, 1);
    expect(
      () => verifyNativeScreenClaim({
        'width': 640,
        'height': 480,
        'distinctSampleColors': 99,
      }, measureNativeScreen(bytes)),
      throwsStateError,
    );
  });
  for (final offset in [0, 2, 10, 14, 18, 22, 26, 28, 30, 34]) {
    test('rejects malformed BMP field $offset', () {
      final bytes = screen();
      bytes[offset] ^= 0x10;
      expect(() => measureNativeScreen(bytes), throwsStateError);
    });
  }
}

Uint8List wave(double frequency, {bool floating = false}) {
  const frames = 8000, rate = 8000;
  final align = floating ? 4 : 2;
  final bytes = Uint8List(58 + frames * align),
      data = ByteData(58 + frames * align);
  void tag(int at, String value) => bytes.setRange(at, at + 4, value.codeUnits);
  void u16(int at, int value) => data.setUint16(at, value, Endian.little);
  void u32(int at, int value) => data.setUint32(at, value, Endian.little);
  u32(4, bytes.length - 8);
  u32(16, 18);
  u16(20, floating ? 3 : 1);
  u16(22, 1);
  u32(24, rate);
  u32(28, rate * align);
  u16(32, align);
  u16(34, align * 8);
  u16(36, 0);
  u32(42, 4);
  u32(46, frames);
  u32(54, frames * align);
  for (var frame = 0; frame < frames; frame++) {
    final value = frequency == 0
        ? 0.0
        : 0.1 * math.sin(2 * math.pi * frequency * frame / rate);
    if (floating) {
      data.setFloat32(58 + frame * align, value, Endian.little);
    } else {
      data.setInt16(58 + frame * align, (value * 32768).round(), Endian.little);
    }
  }
  bytes.setAll(0, data.buffer.asUint8List());
  tag(0, 'RIFF');
  tag(8, 'WAVE');
  tag(12, 'fmt ');
  tag(38, 'fact');
  tag(50, 'data');
  return bytes;
}

Uint8List screen() {
  final bytes = Uint8List(54 + 640 * 480 * 4),
      data = ByteData.sublistView(bytes);
  data.setUint16(0, 0x4d42, Endian.little);
  data.setUint32(2, bytes.length, Endian.little);
  data.setUint32(10, 54, Endian.little);
  data.setUint32(14, 40, Endian.little);
  data.setInt32(18, 640, Endian.little);
  data.setInt32(22, -480, Endian.little);
  data.setUint16(26, 1, Endian.little);
  data.setUint16(28, 32, Endian.little);
  data.setUint32(34, bytes.length - 54, Endian.little);
  for (var at = 54; at < bytes.length; at += 4) {
    bytes[at] = (at ~/ 4) % 256;
  }
  return bytes;
}
