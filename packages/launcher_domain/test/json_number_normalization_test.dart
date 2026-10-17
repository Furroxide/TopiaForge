import 'package:launcher_domain/launcher_domain.dart';
import 'package:launcher_domain/src/json_number_normalization.dart';
import 'package:test/test.dart';

// Every number in a manifest, profile or manager state file is normalized to
// prove that persisting it keeps its value. Manifests can come from third-party
// packages, so normalization has to keep its exact output for every spelling
// and stay linear in the token's length.
void main() {
  test('pins the normalized spelling of representative tokens', () {
    final expected = {
      // Zero in any spelling, including negative zero.
      '0': '0',
      '-0': '0',
      '0.000': '0',
      '-0.0e-5': '0',
      '0E+999': '0',
      // Leading and trailing zeros, in integers and decimals.
      '7': '7:0',
      '-7': '-7:0',
      '100': '1:2',
      '-1200': '-12:2',
      '0.5': '5:-1',
      '0.0012': '12:-4',
      '1.50': '15:-1',
      '10.010': '1001:-2',
      '9007199254740993': '9007199254740993:0',
      // Exponents in either case, with or without a sign.
      '1e2': '1:2',
      '1E+2': '1:2',
      '100e-2': '1:0',
      '-0.0012e3': '-12:-1',
      '120.0e-2': '12:-1',
      '1e-0': '1:0',
      '1e+0000000000000000000005': '1:5',
      '5e-324': '5:-324',
      '1.7976931348623157e308': '17976931348623157:292',
      // Exponents too long for an int, including a carry or a borrow past
      // their last 15 digits.
      '1e123456789012345678901': '1:123456789012345678901',
      '1.5e-123456789012345678901': '15:-123456789012345678902',
      '0.1e1${'0' * 18}': '1:${'9' * 18}',
      '10e${'9' * 21}': '1:1${'0' * 21}',
      '10e-1${'0' * 18}': '1:-${'9' * 18}',
      '0.1e-${'9' * 18}': '1:-1${'0' * 18}',
    };
    for (final MapEntry(key: token, value: normalized) in expected.entries) {
      expect(normalizedJsonNumber(token), normalized, reason: token);
    }
  });

  test('adjusts long exponents exactly as BigInt arithmetic does', () {
    for (final high in ['1', '9', '10', '99', '1000', '9999', '123456']) {
      for (final low in ['0' * 15, '${'0' * 14}7', '${'9' * 14}0', '9' * 15]) {
        for (final sign in ['', '+', '-']) {
          for (final shift in [-20, -1, 0, 1, 20]) {
            final mantissa = shift < 0
                ? '0.${'0' * (-shift - 1)}1'
                : '1${'0' * shift}';
            final token = '${mantissa}e$sign$high$low';
            final exponent =
                BigInt.parse('$sign$high$low') + BigInt.from(shift);
            expect(normalizedJsonNumber(token), '1:$exponent', reason: token);
          }
        }
      }
    }
  });

  test('normalizes hostile 40,000-digit tokens in linear time', () {
    final hostile = {
      // Interior runs of zeros, which an unanchored 0+$ retries from every
      // position.
      '1${'0' * 39998}1': '1${'0' * 39998}1:0',
      '0.1${'0' * 39997}1': '1${'0' * 39997}1:-39999',
      '${'1${'0' * 4999}' * 8}1': '${'1${'0' * 4999}' * 8}1:0',
      // A 200,000-digit exponent, which BigInt converts in quadratic time.
      '1e-${'4' * 200000}': '1:-${'4' * 200000}',
    };
    for (final MapEntry(key: token, value: normalized) in hostile.entries) {
      final watch = Stopwatch()..start();
      final result = normalizedJsonNumber(token);
      final elapsed = watch.elapsedMilliseconds;
      expect(result, normalized);
      expect(
        elapsed,
        lessThan(100),
        reason: 'normalizing a ${token.length}-character token',
      );
    }
  });

  test('refuses a hostile manifest number in linear time', () {
    final text = '{"weight": 0.1${'0' * 39997}1}';
    final watch = Stopwatch()..start();
    expect(
      () => decodeJsonPreservingValues(text, label: 'Manifest'),
      throwsFormatException,
    );
    expect(watch.elapsedMilliseconds, lessThan(100));
  });

  test('accepts long spellings of values it can preserve', () {
    final text =
        '{"a": 1e+${'0' * 40000}5, "b": 0e-${'9' * 40000}, '
        '"c": 2.5${'0' * 40000}e-3}';
    expect(decodeJsonPreservingValues(text, label: 'Manifest'), {
      'a': 100000.0,
      'b': 0.0,
      'c': 0.0025,
    });
  });
}
