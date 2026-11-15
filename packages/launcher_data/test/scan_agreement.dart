import 'dart:math';

import 'package:test/test.dart';

/// A gross bound on one scan of the full input. Backtracking patterns spent
/// seconds on these inputs; only a clearly quadratic scan reaches it.
const scanBudget = Duration(milliseconds: 100);

/// How much a linear scan's time may grow when its input grows four times.
/// A linear scan grows about four times and a quadratic one about sixteen
/// times; the limit sits at their geometric mean, so either may be off by a
/// factor of two, from a scan's fixed cost or an uneven step, before the
/// check misjudges it.
const linearGrowthLimit = 8.0;

/// The most characters in an input [expectLinear] times. At most 16 KB even
/// as a two-byte string: half of a 32 KB L1 data cache, the smallest on
/// current desktop and server cores.
const maxTimedChars = 8192;

/// The scale at which [expectWithinBudget] callers build their full input.
const fullScale = 8;

const _rounds = 10;

/// The bounds of [match], or null.
({int start, int end})? matchBounds(RegExpMatch? match) =>
    match == null ? null : (start: match.start, end: match.end);

/// Checks that [scan] runs in linear time on the texts [input] builds from a
/// repeat count, and returns its result on the full input, `input(fullCount)`.
///
/// The best of three scans of the full input must stay under [scanBudget].
/// Growth is timed on two smaller inputs, `input(n)` (N) and `input(4 * n)`
/// (4N), where n is the largest power of two whose 4N input has at most
/// [maxTimedChars] characters. Sizing them by characters rather than by count
/// keeps both in a core's L1 data cache however long the repeated text is, so
/// a linear scan costs the same per character at both sizes: a larger input
/// that spills to a slower cache level raises that cost, which a ratio of
/// timings cannot tell from superlinear growth. The best timing at 4N must
/// stay under [linearGrowthLimit] times the best at N. Each timing repeats the
/// scan for about 2 ms, and the two sizes alternate over several rounds, so
/// neither the timer's resolution nor a slow stretch of a shared runner
/// decides the result.
T expectLinear<T>(
  String Function(int count) input,
  T Function(String text) scan, {
  required int fullCount,
  String? reason,
}) {
  final full = input(fullCount);
  final result = scan(full);
  var fullBest = -1;
  for (var timing = 0; timing < 3; timing++) {
    final micros = _timed(() => scan(full), 1);
    if (fullBest < 0 || micros < fullBest) fullBest = micros;
  }
  var count = 1;
  while (input(8 * count).length <= maxTimedChars) {
    count *= 2;
  }
  final text = input(count);
  final quadrupled = input(4 * count);
  expect(
    quadrupled.length,
    lessThanOrEqualTo(maxTimedChars),
    reason:
        '${reason == null ? '' : '$reason; '}'
        'the 4N input at count 1 exceeds $maxTimedChars characters; '
        'repeat a shorter text',
  );
  scan(text);
  scan(quadrupled);
  // The best of three single scans sets the repeat count, so one interrupted
  // scan cannot cut every timing down to a few microseconds.
  var once = -1;
  for (var timing = 0; timing < 3; timing++) {
    final micros = _timed(() => scan(text), 1);
    if (once < 0 || micros < once) once = micros;
  }
  final repeats = (2000 / max(1, once)).ceil().clamp(1, 2000);
  // Alternate short timings of the two sizes many times and keep the best of
  // each, so a busy stretch of a shared runner cannot slow only one size.
  var small = -1;
  var large = -1;
  for (var round = 0; round < _rounds; round++) {
    final n = _timed(() => scan(text), repeats);
    final four = _timed(() => scan(quadrupled), repeats);
    if (small < 0 || n < small) small = n;
    if (large < 0 || four < large) large = four;
  }
  final detail =
      '${reason == null ? '' : '$reason; '}'
      'full input: $fullBest us; N: ${text.length} and 4N: '
      '${quadrupled.length} characters; $repeats scans per timing; '
      'N: $small us; 4N: $large us';
  expect(
    fullBest,
    lessThan(scanBudget.inMicroseconds),
    reason:
        'one scan of the full input exceeded '
        '${scanBudget.inMilliseconds} ms; $detail',
  );
  expect(
    large / max(1, small),
    lessThan(linearGrowthLimit),
    reason: 'quadrupling the input grew the time more than linearly; $detail',
  );
  return result;
}

/// Checks only that the best of three scans of [text] stays under
/// [scanBudget]. For scans whose time steps with allocation and collection
/// as the input grows, where a ratio of timings is not a stable measure.
T expectWithinBudget<T>(
  String text,
  T Function(String text) scan, {
  String? reason,
}) {
  final result = scan(text);
  var best = -1;
  for (var timing = 0; timing < 3; timing++) {
    final micros = _timed(() => scan(text), 1);
    if (best < 0 || micros < best) best = micros;
  }
  expect(
    best,
    lessThan(scanBudget.inMicroseconds),
    reason: '${reason == null ? '' : '$reason; '}best of three scans: $best us',
  );
  return result;
}

int _timed(void Function() scan, int repeats) {
  final watch = Stopwatch()..start();
  for (var repeat = 0; repeat < repeats; repeat++) {
    scan();
  }
  watch.stop();
  return watch.elapsedMicroseconds;
}

/// Seeded strings of up to 16 [tokens], so a disagreement reproduces.
List<String> generatedSamples(List<String> tokens, {required int seed}) {
  final random = Random(seed);
  return [
    for (var sample = 0; sample < 4000; sample++)
      [
        for (var token = random.nextInt(17); token > 0; token--)
          tokens[random.nextInt(tokens.length)],
      ].join(),
  ];
}

/// Checks [scan] against [oracle] on every sample, and that the samples
/// exercise both matches and misses.
void expectAgreement<T extends Object>(
  List<String> samples,
  T? Function(String text) scan,
  T? Function(String text) oracle,
) {
  var matches = 0;
  for (final sample in samples) {
    final expected = oracle(sample);
    expect(scan(sample), expected, reason: 'input: ${_escape(sample)}');
    if (expected != null) matches++;
  }
  expect(matches, inInclusiveRange(200, samples.length - 200));
}

String _escape(String text) => text.codeUnits
    .map(
      (unit) => unit >= 0x20 && unit < 0x7F
          ? String.fromCharCode(unit)
          : '\\u{${unit.toRadixString(16)}}',
    )
    .join();
