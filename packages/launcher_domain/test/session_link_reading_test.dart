import 'package:launcher_domain/launcher_domain.dart';
import 'package:test/test.dart';

import 'multiplayer_fixtures.dart';

void main() {
  final fixture = readMultiplayerFixture('links/cases.json');
  const server =
      'c5f39060271ebd2f22d9bf155a26bf459274e31b795145fd9d862dc206d98bb3';

  group('reading shared link fixtures', () {
    for (final item in asObjects(fixture['valid'])) {
      final input = item['input']! as String;
      test('reads $input as the link tryParse returns', () {
        final reading = SessionLink.read(input);
        expect(reading.link, isNotNull);
        expect(reading.link, SessionLink.tryParse(input));
        expect(reading.refusal, isNull);
      });
    }

    test('refuses every invalid fixture with the matching reason', () {
      for (final item in asObjects(fixture['invalid'])) {
        final input = item['input']! as String;
        final reading = SessionLink.read(input);
        final expected = input.length > SessionLink.maximumLength
            ? SessionLinkRefusal.tooLong
            : input.startsWith('topiaforge:')
            ? SessionLinkRefusal.invalid
            : SessionLinkRefusal.notSessionLink;
        expect(reading.link, isNull, reason: item['about']! as String);
        expect(reading.refusal, expected, reason: item['about']! as String);
      }
    });
  });

  group('session link refusals', () {
    test('nothing is not a session link', () {
      expect(SessionLink.read(null).refusal, SessionLinkRefusal.notSessionLink);
      expect(SessionLink.read('').refusal, SessionLinkRefusal.notSessionLink);
      expect(
        SessionLink.read('https://example.com/join/ABCDE12345').refusal,
        SessionLinkRefusal.notSessionLink,
      );
      expect(
        SessionLink.read('topiaforge').refusal,
        SessionLinkRefusal.notSessionLink,
      );
    });

    test('a topiaforge link that does not parse is invalid', () {
      for (final input in [
        'topiaforge:',
        'topiaforge://join/',
        'TOPIAFORGE://join/BAD',
        'topiaforge://settings',
        'topiaforge://join/server/$server/extra',
        'topiaforge://join/direct/$server/192.168.1.20/0',
      ]) {
        expect(
          SessionLink.read(input).refusal,
          SessionLinkRefusal.invalid,
          reason: input,
        );
      }
    });

    test('the length bound is 512 characters, checked before parsing', () {
      final atLimit = 'topiaforge://join/'.padRight(
        SessionLink.maximumLength,
        'A',
      );
      final overLimit = '${atLimit}A';
      expect(atLimit.length, SessionLink.maximumLength);
      expect(SessionLink.read(atLimit).refusal, SessionLinkRefusal.invalid);
      expect(SessionLink.read(overLimit).refusal, SessionLinkRefusal.tooLong);
      // Huge text is refused for its length, whatever it holds.
      expect(
        SessionLink.read('x' * 1000000).refusal,
        SessionLinkRefusal.tooLong,
      );
      final validDirect =
          'topiaforge://join/direct/$server/'
          '${'a' * 60}.${'b' * 60}.${'c' * 60}.example/27250';
      expect(SessionLink.read(validDirect).link, isNotNull);
      expect(
        SessionLink.read('$validDirect${'?' * 400}').refusal,
        SessionLinkRefusal.tooLong,
      );
    });

    test('a refusal carries no part of the text', () {
      const direct = 'topiaforge://join/direct/$server/192.168.1.20/0';
      final reading = SessionLink.read(direct);
      expect(reading.link, isNull);
      expect(reading.refusal.toString(), isNot(contains('192.168')));
    });
  });

  group('the topiaforge scheme', () {
    test('matches in any ASCII case', () {
      expect(SessionLink.hasScheme('topiaforge:'), isTrue);
      expect(SessionLink.hasScheme('TopiaForge://join/ABCDE12345'), isTrue);
      expect(SessionLink.hasScheme('TOPIAFORGE:anything'), isTrue);
    });

    test('is exactly topiaforge followed by a colon', () {
      for (final input in [
        null,
        '',
        'topiaforge',
        'topiaforgex:',
        'topiaforg:',
        ' topiaforge:',
        'xtopiaforge:',
        'topia-forge:',
        'topiaforge//join',
        'https://topiaforge:',
      ]) {
        expect(SessionLink.hasScheme(input), isFalse, reason: '$input');
      }
    });

    test('no other script stands in for its letters', () {
      for (final input in [
        'TOPİAFORGE:', // dotted capital I lowercases to i plus a dot.
        'ｔopiaforge:', // fullwidth t
        'topiaforgе:', // Cyrillic e
      ]) {
        expect(SessionLink.hasScheme(input), isFalse, reason: input);
        expect(
          SessionLink.read(input).refusal,
          SessionLinkRefusal.notSessionLink,
        );
      }
    });
  });
}
