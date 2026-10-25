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
      // Written out in full, as long as an address without a dotted tail is.
      '2001:0db8:0000:0000:0000:0000:0000:0077',
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

  test(
    'a cut line keeps whole words, and no part of one reads as an address',
    () {
      expect(
        withoutAddresses('peer 203.0.113.7 joined', maxLength: 64),
        'peer $withheldAddress joined',
      );
      // Each word below is no address, but cut after its eleventh or twelfth
      // letter it would read 203.0.113.7 or 203.0.113.77. Whatever the limit,
      // a word it would cut is left out whole.
      for (final word in ['203.0.113.777', '203.0.113.7777']) {
        final line = '${'word ' * 6}$word tail';
        for (var limit = 30; limit <= 30 + word.length; limit++) {
          expect(
            withoutAddresses(line, maxLength: limit),
            'word word word word word word…',
            reason: '$word cut at $limit',
          );
        }
      }
      // An address past the limit is never read, let alone kept.
      final long = withoutAddresses(
        '${'x ' * 3000}203.0.113.7',
        maxLength: 4096,
      );
      expect(long, isNot(contains('203.0.113')));
      expect(long.length, lessThanOrEqualTo(4096));
      expect(long, endsWith('…'));
      // What stands in for an address is longer than ::1, and the line still
      // fits.
      final loopbacks = withoutAddresses('::1 ' * 40, maxLength: 100);
      expect(loopbacks.length, lessThanOrEqualTo(100));
      expect(loopbacks, isNot(contains('::1')));
      expect(loopbacks, endsWith('…'));
      expect(() => withoutAddresses('x', maxLength: 0), throwsRangeError);
    },
  );

  test(
    'a megabyte of host text costs no more than the words a cut line keeps',
    () {
      // Hex digits and colons make the scrubber try every candidate: read
      // whole, this much text takes over a minute. The scrub runs without a
      // pause, so a test timeout could not stop it; the time is measured.
      final text = '${'2:' * 2000} ' * 250;
      final watch = Stopwatch()..start();
      final line = withoutAddresses(text, maxLength: 4096);
      expect(watch.elapsed, lessThan(const Duration(seconds: 10)));
      expect(line.length, lessThanOrEqualTo(4096));
      expect(line, endsWith('…'));
    },
  );
}
