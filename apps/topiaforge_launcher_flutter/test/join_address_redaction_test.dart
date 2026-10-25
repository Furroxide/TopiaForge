import 'package:flutter_test/flutter_test.dart';
import 'package:topiaforge_launcher_flutter/src/join_texts.dart';

void main() {
  test('host text cannot expose trailing-compressed IPv6 addresses', () {
    for (final address in ['2001:db8::', '::', 'fe80::%4']) {
      for (final suffix in ['', '.', '!', '?', ':']) {
        final text = 'Server $address$suffix is unavailable';
        expect(withoutAddresses(text), contains(withheldAddress));
        expect(withoutAddresses(text), isNot(contains(address)));
      }
    }
  });

  test('IPv6 embedded in host text is withheld without hiding mod ids', () {
    for (final text in [
      'https://[2001:db8::]/status',
      'peer=2001:db8::77/path',
      'ip=2001:db8::',
    ]) {
      expect(withoutAddresses(text), contains(withheldAddress));
      expect(withoutAddresses(text), isNot(contains('2001:db8')));
    }
    const ordinary = 'tf.sandbox@1.4.0, Sam: hello! SHA-256: abc123';
    expect(withoutAddresses(ordinary), ordinary);
  });
}
