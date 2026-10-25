import 'dart:math';

import 'package:launcher_domain/launcher_domain.dart';
import 'package:test/test.dart';

void main() {
  test('address literals survive no host-written prefix or suffix', () {
    for (final address in [
      '203.0.113.77',
      '2001:db8::',
      '2001:db8::77',
      'fe80::1%4',
      'fd00::abcd',
      '::1',
      '::ffff:203.0.113.77',
      '::ffff:cb00:714d',
      '64:ff9b::cb00:714d',
      '0064:ff9b:1:2:3:4:5:6',
      '2001:db8:1:2:3:4:5:77',
    ]) {
      for (final prefix in ['', 'peer:', 'peer.', 'v1.2.3:', 'ip=', 'dead']) {
        for (final suffix in [
          '',
          '.',
          ':27250',
          '.example.net',
          '.dead',
          '.1',
          '.${'a' * 100}',
        ]) {
          final input = '$prefix$address$suffix';
          expect(
            withoutAddresses(input),
            isNot(contains(address)),
            reason: input,
          );
          expect(
            withoutAddresses(input),
            contains(withheldAddress),
            reason: input,
          );
        }
      }
    }
  });

  test('seeded host text with 20000 peer addresses is withheld', () {
    final random = Random(310003);
    var leaked = 0;
    for (var i = 0; i < 20000; i++) {
      final h = random.nextInt(65536).toRadixString(16);
      final v4 = '203.0.113.${1 + random.nextInt(254)}';
      final address = [
        v4,
        '2001:db8:$h::',
        '2001:db8::$h',
        'fe80::$h%4',
        '::ffff:$v4',
        '64:ff9b::$h',
        'fd12:3456::$h',
        '2001:db8:1:2:3:4:5:$h',
      ][i % 8];
      final input = [
        'peer:$address',
        'peer.$address',
        'ip=$address',
        '[$address]:27250',
        '$address!',
        '$address.example.net',
        'dead$address',
        '$address.1',
      ][random.nextInt(8)];
      final output = withoutAddresses(input);
      if (output.contains(address) || !output.contains(withheldAddress)) {
        leaked++;
      }
    }
    expect(leaked, 0, reason: 'seed 310003, all 20000 cases checked');
  });

  test('2311 server names keep colons that are not peer addresses', () {
    final random = Random(310003);
    var changed = 0;
    for (var i = 0; i < 2311; i++) {
      final name = [
        "Sam's Sandbox :: $i :: EU",
        'Cafe::Bar $i',
        'Dead::Beef $i',
        'Server::${i + 2}',
        'Arena $i: hello',
        '::Arena::$i',
        'tf.sandbox 1.4.0 lobby $i',
      ][random.nextInt(7)];
      if (withoutAddresses(name) != name) changed++;
    }
    expect(changed, 0, reason: 'all 2311 names checked');
    for (final name in ['[::]:27250', 'Robo::Arena', '::Face::']) {
      expect(withoutAddresses(name), name);
    }
  });

  test('a numeric suffix cannot disguise IPv4 as a longer version', () {
    for (final text in [
      '203.0.113.77.1',
      '1.0.0-peer.203.0.113.77.1',
      '1.2.3.4.5',
    ]) {
      expect(withoutAddresses(text), withheldAddress);
    }
    expect(withoutAddresses('tf.sandbox@1.4.0'), 'tf.sandbox@1.4.0');
  });
}
