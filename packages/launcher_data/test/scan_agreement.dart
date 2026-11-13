import 'dart:math';

import 'package:test/test.dart';

/// A gross bound on one scan of the full input. Backtracking patterns spent
/// seconds on these inputs; only a clearly quadratic scan reaches it.
const scanBudget = Duration(milliseconds: 100);

/// How much a linear scan's time may grow when its input doubles. A quadratic
/// scan grows about four times.
const linearGrowthLimit = 3.0;

const _rounds = 10;

/// The scale of each test's full input; growth is timed at scales 1 and 2.
const fullScale = 8;

/// The bounds of [match], or null.
({int start, int end})? matchBounds(RegExpMatch? match) =>
    match == null ? null : (start: match.start, end: match.end);

/// Checks that [scan] runs in linear time on [input], and returns its result
/// on the full input, `input(fullScale)`.
///
/// The best of three scans of the full input must stay under [scanBudget].
/// Growth is timed on the smaller inputs at scale 1 (N) and scale 2 (2N),
/// which stay in a core's private cache, so other work on a shared runner
/// cannot slow only the larger one: the best timing at 2N must stay under
/// [linearGrowthLimit] times the best at N. Each timing repeats the scan for
/// about 2 ms, and the two sizes alternate over several rounds, so neither the
/// timer's resolution nor a slow stretch of the runner decides the result. A
/// quadratic scan still grows about four times at these sizes.
T expectLinear<T>(
  String Function(int scale) input,
  T Function(String text) scan, {
  String? reason,
}) {
  final full = input(fullScale);
  final result = scan(full);
  var fullBest = -1;
  for (var timing = 0; timing < 3; timing++) {
    final micros = _timed(() => scan(full), 1);
    if (fullBest < 0 || micros < fullBest) fullBest = micros;
  }
  final text = input(1);
  final doubled = input(2);
  scan(text);
  scan(doubled);
  final once = Stopwatch()..start();
  scan(text);
  once.stop();
  final repeats = (2000 / max(1, once.elapsedMicroseconds)).ceil().clamp(
    1,
    2000,
  );
  // Alternate short timings of the two sizes many times and keep the best of
  // each, so a busy stretch of a shared runner cannot slow only one size.
  var small = -1;
  var large = -1;
  for (var round = 0; round < _rounds; round++) {
    final n = _timed(() => scan(text), repeats);
    final twice = _timed(() => scan(doubled), repeats);
    if (small < 0 || n < small) small = n;
    if (large < 0 || twice < large) large = twice;
  }
  final detail =
      '${reason == null ? '' : '$reason; '}'
      'full input: $fullBest us; '
      '$repeats scans per timing; N: $small us; 2N: $large us';
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
    reason: 'doubling the input grew the time more than linearly; $detail',
  );
  return result;
}

/// Checks only that the best of three scans of [text] stays under
/// [scanBudget]. For scans whose time steps with allocation and collection
/// as the input grows, where a ratio at N and 2N is not a stable measure.
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
