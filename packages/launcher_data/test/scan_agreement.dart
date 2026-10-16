import 'dart:math';

import 'package:test/test.dart';

/// How long a scanner may take on input a backtracking pattern spent
/// seconds on.
const scanBudget = Duration(milliseconds: 100);

/// The bounds of [match], or null.
({int start, int end})? matchBounds(RegExpMatch? match) =>
    match == null ? null : (start: match.start, end: match.end);

/// Runs [scan] and fails when it takes longer than [scanBudget].
T withinBudget<T>(T Function() scan) {
  final watch = Stopwatch()..start();
  final result = scan();
  watch.stop();
  expect(watch.elapsed, lessThan(scanBudget));
  return result;
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
