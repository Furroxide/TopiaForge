import 'package:launcher_domain/launcher_domain.dart';
import 'package:test/test.dart';

import 'multiplayer_fixtures.dart';

void main() {
  final fixture = readMultiplayerFixture('links/cases.json');
  const server =
      'c5f39060271ebd2f22d9bf155a26bf459274e31b795145fd9d862dc206d98bb3';

  group('shared link fixtures', () {
    test('the invite alphabet is Crockford base32', () {
      expect(fixture['alphabet'], InviteCode.alphabet);
    });

    for (final item in asObjects(fixture['valid'])) {
      final input = item['input']! as String;
      test('parses $input', () {
        final link = SessionLink.tryParse(input);
        expect(link, isNotNull);
        expect(link!.kind.name, item['kind']);
        expect(link.inviteCode, item['inviteCode']);
        expect(link.serverId, item['serverId']);
        expect(link.host, item['host']);
        expect(link.port, item['port']);
        expect(link.canonical, item['canonical']);
        expect(SessionLink.tryParse(link.canonical)?.canonical, link.canonical);
        expect(SessionLink.tryParse(link.canonical), link);
      });
    }

    test('refuses every invalid link', () {
      final cases = asObjects(fixture['invalid']);
      expect(cases.length, greaterThanOrEqualTo(25));
      for (final item in cases) {
        expect(
          SessionLink.tryParse(item['input']! as String),
          isNull,
          reason: item['about']! as String,
        );
      }
    });

    test('normalizes invite codes as the fixtures say', () {
      for (final item in asObjects(fixture['inviteCodes'])) {
        final code = InviteCode.tryNormalize(item['input']! as String);
        expect(code, item['canonical'], reason: item['input']! as String);
        if (code != null) expect(InviteCode.display(code), item['display']);
      }
    });
  });

  group('session links', () {
    test('nothing is not a link', () {
      expect(SessionLink.tryParse(null), isNull);
      expect(SessionLink.tryParse(''), isNull);
      expect(SessionLink.tryParse('topiaforge://join'), isNull);
      expect(SessionLink.tryParse('topiaforge:/join/ABCDE12345'), isNull);
      expect(SessionLink.tryParse('topiaforge://join/server/'), isNull);
      expect(
        SessionLink.tryParse('topiaforge://join/direct/$server//1'),
        isNull,
      );
      expect(
        SessionLink.tryParse('topiaforge://join/direct/$server/a.b/1/2'),
        isNull,
      );
      expect(SessionLink.tryParse('topiaforge://join/other/$server'), isNull);
      expect(
        SessionLink.tryParse('topiaforge://join/ABCDEé12345'),
        isNull,
        reason: 'non-ASCII',
      );
    });

    test('only the scheme ignores case', () {
      expect(SessionLink.tryParse('TOPIAFORGE://join/ABCDE12345'), isNotNull);
      expect(SessionLink.tryParse('topiaforge://Join/ABCDE12345'), isNull);
      expect(SessionLink.tryParse('topiaforge://join/SERVER/$server'), isNull);
      expect(
        SessionLink.tryParse('topiaforge://join/Direct/$server/a.org/1'),
        isNull,
      );
    });

    test('a direct link keeps its address out of toString', () {
      final link = SessionLink.direct(
        serverId: server,
        host: '192.168.1.20',
        port: 27250,
      );
      expect(link.canonical, contains('192.168.1.20'));
      expect(link.toString(), isNot(contains('192.168.1.20')));
      expect(link.toString(), isNot(contains('27250')));
      expect(link.toString(), contains(server));
      final invite = SessionLink.invite('abcde-12345');
      expect(invite.toString(), invite.canonical);
      expect(SessionLink.server(server).toString(), contains(server));
    });

    test('constructors refuse what parsing refuses', () {
      expect(
        () => SessionLink.direct(
          serverId: 'a' * 64,
          host: 'example.org',
          port: 0,
        ),
        throwsRangeError,
      );
      expect(
        () => SessionLink.direct(
          serverId: server,
          host: 'example.org',
          port: 65536,
        ),
        throwsRangeError,
      );
      expect(
        () => SessionLink.direct(serverId: server, host: 'bad_host', port: 1),
        throwsArgumentError,
      );
      expect(
        () => SessionLink.server(server.toUpperCase()),
        throwsArgumentError,
      );
      expect(() => SessionLink.invite('ABCD'), throwsArgumentError);
      expect(
        SessionLink.direct(
          serverId: server,
          host: 'Play.Example.ORG',
          port: 1,
        ).host,
        'play.example.org',
      );
    });
  });

  group('invite codes', () {
    test('reading is forgiving within ASCII', () {
      const accepted = {
        'abcde-12345': 'ABCDE12345',
        ' ABCDE 12345 ': 'ABCDE12345',
        'A-B-C-D-E-1-2-3-4-5': 'ABCDE12345',
        'oOiIlL0123': '0011110123',
        'zzzzz-zzzzz': 'ZZZZZZZZZZ',
      };
      accepted.forEach((input, code) {
        expect(InviteCode.tryNormalize(input), code, reason: input);
      });
      for (final input in [
        'ABCDEU2345',
        'ABCD',
        'ABCDE_12345',
        'ABCDE123456',
        // Only ASCII letters change case. .NET's ToUpperInvariant would read
        // a dotless i as I (so 1) and a long s as S.
        'ıBCDE12345',
        'ſBCDE12345',
        'ＡBCDE12345',
        '${'-' * 23}ABCDE12345',
        '',
      ]) {
        expect(InviteCode.tryNormalize(input), isNull, reason: input);
      }
      expect(InviteCode.tryNormalize('${'-' * 22}ABCDE12345'), 'ABCDE12345');
      expect(InviteCode.tryNormalize(null), isNull);
    });

    test('only canonical codes are displayed', () {
      expect(InviteCode.isCanonical('ABCDE12345'), isTrue);
      expect(InviteCode.isCanonical('abcde12345'), isFalse);
      expect(InviteCode.isCanonical('ABCDE-12345'), isFalse);
      expect(() => InviteCode.display('abcde12345'), throwsArgumentError);
      expect(InviteCode.display('ABCDE12345'), 'ABCDE-12345');
    });
  });

  group('hosts', () {
    test('IPv6 addresses follow RFC 5952', () {
      const cases = {
        '2001:0db8:0000:0000:0000:0000:0000:0001': '2001:db8::1',
        '2001:db8:0:1:1:1:1:1': '2001:db8:0:1:1:1:1:1',
        '2001:db8:0:0:1:0:0:1': '2001:db8::1:0:0:1',
        '2001:0:0:1:0:0:0:1': '2001:0:0:1::1',
        '1:0:0:2:0:0:3:4': '1::2:0:0:3:4',
        '0:0:0:0:0:0:0:0': '::',
        '::': '::',
        '0:0:0:0:0:0:0:1': '::1',
        '1:0:0:0:0:0:0:0': '1::',
        '1:2:3:4:5:6:7::': '1:2:3:4:5:6:7:0',
        '::2:3:4:5:6:7:8': '0:2:3:4:5:6:7:8',
        'FE80::ABCD': 'fe80::abcd',
        '::ffff:192.0.2.1': '192.0.2.1',
        '::FFFF:c000:0201': '192.0.2.1',
        '::1.2.3.4': '::102:304',
        '1:2:3:4:5:6:1.2.3.4': '1:2:3:4:5:6:102:304',
        '64:ff9b::192.0.2.1': '64:ff9b::c000:201',
      };
      cases.forEach((input, expected) {
        expect(NetworkHost.tryNormalize(input), expected, reason: input);
      });
      for (final input in [
        'fe80::1%eth0',
        '[::1]',
        '1:2:3:4:5:6:7:8:9',
        '1:2:3:4:5:6:7',
        '1:2:3:4:5:6:7:8::',
        '::1:2:3:4:5:6:7:8',
        '::g',
        'gggg::1',
        '1::2::3',
        ':::',
        ':',
        '1:',
        ':1',
        '12345::',
        '::ffff:010.0.0.7',
        '::256.0.0.1',
        '::1.2.3',
        '1.2.3.4::',
        '1.2.3.4:1::',
      ]) {
        expect(NetworkHost.tryNormalize(input), isNull, reason: input);
      }
    });

    test('IPv4 addresses are dotted decimal without leading zeros', () {
      expect(NetworkHost.tryNormalize('192.168.1.20'), '192.168.1.20');
      expect(NetworkHost.tryNormalize('0.0.0.0'), '0.0.0.0');
      expect(NetworkHost.tryNormalize('255.255.255.255'), '255.255.255.255');
      for (final input in [
        '192.168.001.20',
        '256.1.1.1',
        '1.2.3.4.5',
        '1.2.3',
        '999.1.1.1',
        '123',
        '1..2.3',
      ]) {
        expect(NetworkHost.tryNormalize(input), isNull, reason: input);
      }
    });

    test('names are lowercase letter-digit-hyphen labels', () {
      const accepted = {
        'Play.Example.ORG': 'play.example.org',
        'host.3com': 'host.3com',
        '3com': '3com',
        '1-2.3': '1-2.3',
        'localhost': 'localhost',
        'xn--bcher-kva.example': 'xn--bcher-kva.example',
      };
      accepted.forEach((input, expected) {
        expect(NetworkHost.tryNormalize(input), expected, reason: input);
      });
      final label = 'a' * 63;
      expect(NetworkHost.tryNormalize('$label.org'), '$label.org');
      final longest = [label, label, label, 'a' * 61].join('.');
      expect(longest, hasLength(253));
      expect(NetworkHost.tryNormalize(longest), longest);
      for (final input in [
        '${label}a.org',
        '${longest}a',
        'bad_host.example',
        '-bad.example',
        'bad-.example',
        'example.com.',
        '.example.com',
        'a..b',
        'exa mple.org',
        // Only ASCII letters change case: a Kelvin sign or a dotted capital I
        // is not a k or an i here, though .NET's ToLowerInvariant maps them.
        'Kexample.org',
        'İnstance.org',
        'bücher.example',
        '',
      ]) {
        expect(NetworkHost.tryNormalize(input), isNull, reason: input);
      }
      expect(NetworkHost.tryNormalize(null), isNull);
    });
  });
}
