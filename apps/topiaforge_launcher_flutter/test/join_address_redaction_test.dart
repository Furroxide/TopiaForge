import 'package:flutter_test/flutter_test.dart';
import 'package:topiaforge_launcher_flutter/src/join_texts.dart';

void main() {
  test('host text cannot expose trailing-compressed IPv6 addresses', () {
    for (final address in ['2001:db8::', 'fe80::%4']) {
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
      // After a label's colon or dot, with or without a port.
      'peer:2001:db8::1',
      'ip:2001:db8::7.',
      'peer.2001:db8::1',
      'v1.2.3:2001:db8::1',
      'addr:2001:db8::1:27250',
    ]) {
      expect(withoutAddresses(text), contains(withheldAddress), reason: text);
      expect(withoutAddresses(text), isNot(contains('2001:db8')), reason: text);
    }
    const ordinary = 'tf.sandbox@1.4.0, Sam: hello! SHA-256: abc123';
    expect(withoutAddresses(ordinary), ordinary);
  });

  test('every range a peer address comes from is withheld', () {
    for (final address in [
      'host:fe80::1%4',
      'fd00::abcd',
      '::1',
      '::ffff:cb00:7107',
      '64:ff9b::cb00:7107',
    ]) {
      expect(withoutAddresses(address), withheldAddress, reason: address);
    }
  });

  test('server names keep colons that hold no address a peer could have', () {
    // `::` names no one, and words such as "Cafe" make no address in use.
    for (final name in [
      "Sam's Sandbox :: 24/7 :: EU",
      '::Arena::',
      'Robo::Arena',
      'Server::2',
      'Cafe::Bar',
      'Dead::Beef',
      '::Face::',
      '[::]:27250',
    ]) {
      expect(withoutAddresses(name), name);
    }
  });

  test('IPv4 glued to a dotted name is withheld, a longer version is not', () {
    for (final text in ['peer.203.0.113.7', '203.0.113.7.example.net']) {
      expect(withoutAddresses(text), withheldAddress, reason: text);
    }
    for (final version in ['1.2.3.4.5', '2026.10.25', 'tf.sandbox 1.4.0']) {
      expect(withoutAddresses(version), version);
    }
  });
}
