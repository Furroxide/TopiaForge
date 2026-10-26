import 'package:launcher_domain/launcher_domain.dart';
import 'package:test/test.dart';

void main() {
  test('a cut keeps the address placeholder whole or leaves it out', () {
    for (final prefix in ['', 'peer ', 'one two ', 'one,']) {
      final text = '$prefix::1 tail';
      final complete = withoutAddresses(text);
      for (var limit = 1; limit <= complete.length + 1; limit++) {
        final actual = withoutAddresses(text, maxLength: limit);
        expect(actual.length, lessThanOrEqualTo(limit));
        expect(
          actual.replaceAll(withheldAddress, ''),
          isNot(contains('address')),
          reason: '$text at $limit: $actual',
        );
        expect(
          complete.startsWith(
            actual.endsWith('…')
                ? actual.substring(0, actual.length - 1)
                : actual,
          ),
          isTrue,
          reason: 'Kept words have their original order and occur once.',
        );
      }
    }
  });

  test('a bound that fits the text and replacements changes nothing', () {
    for (final text in [
      '',
      'one two three',
      'peer ::1 joined',
      'peer [2001:db8::77]:27250 joined',
      '203.0.113.7777 tail',
      'Cafe::Bar 1.2.3',
      'one,::1;two',
    ]) {
      final complete = withoutAddresses(text);
      final limit = text.length + complete.length + 1;
      expect(withoutAddresses(text, maxLength: limit), complete);
    }
  });
}
