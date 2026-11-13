import 'package:test/test.dart';

import 'scan_agreement.dart';

void main() {
  group('expectLinear', () {
    test('accepts a scan whose time grows with its input', () {
      expect(expectLinear((scale) => 'ab' * (12500 * scale), _countB), 100000);
    });

    test('refuses a scan that compares every pair of characters', () {
      expect(
        () => expectLinear((scale) => 'ab' * (100 * scale), _countEqualPairs),
        throwsA(
          isA<TestFailure>().having(
            (failure) => failure.message,
            'message',
            contains('more than linearly'),
          ),
        ),
      );
    });
  });
}

int _countB(String text) {
  var count = 0;
  for (var index = 0; index < text.length; index++) {
    if (text.codeUnitAt(index) == 0x62) count++;
  }
  return count;
}

int _countEqualPairs(String text) {
  var count = 0;
  for (var first = 0; first < text.length; first++) {
    for (var second = first + 1; second < text.length; second++) {
      if (text.codeUnitAt(first) == text.codeUnitAt(second)) count++;
    }
  }
  return count;
}
