import 'package:launcher_domain/launcher_domain.dart';
import 'package:test/test.dart';

void main() {
  group('RobotopiaGameVersion', () {
    test('maps launcher build ids to the canonical SemVer namespace', () {
      expect(RobotopiaGameVersion.tryFromBuildId(2227), '0.0.2227');
      expect(RobotopiaGameVersion.tryFromBuildId('2227'), '0.0.2227');
      expect(RobotopiaGameVersion.tryFromBuildLabel('Build 2227'), '0.0.2227');
      expect(RobotopiaGameVersion.tryBuildId('0.0.2227'), 2227);
      expect(RobotopiaGameVersion.tryBuildLabel('0.0.2227'), 'build 2227');
    });

    test('rejects noncanonical or out-of-range build provenance', () {
      for (final value in <Object?>[
        null,
        true,
        0,
        -1,
        1.5,
        '',
        '02227',
        ' 2227',
        '22.27',
        2147483648,
        '999999999999999999999999999999999999999',
      ]) {
        expect(
          RobotopiaGameVersion.tryFromBuildId(value),
          isNull,
          reason: '$value',
        );
      }
      for (final value in [
        '1.0.2227',
        '0.1.2227',
        '0.0.0',
        '0.0.2227-preview',
        '0.0.2227+local',
        '0.0.2147483648',
      ]) {
        expect(RobotopiaGameVersion.tryBuildId(value), isNull, reason: value);
      }
    });
  });

  group('SemanticVersion.parse', () {
    test('round-trips prerelease and build metadata', () {
      const source = '1.2.3-alpha.1-rc+build.001.sha-abcdef';
      final version = SemanticVersion.parse(source);

      expect(version.major, 1);
      expect(version.minor, 2);
      expect(version.patch, 3);
      expect(version.prerelease, 'alpha.1-rc');
      expect(version.buildMetadata, 'build.001.sha-abcdef');
      expect(version.isPrerelease, isTrue);
      expect(version.toString(), source);
      expect(SemanticVersion.parse(version.toString()), version);
    });

    test('implements the SemVer prerelease precedence sequence', () {
      final versions = [
        '1.0.0-alpha',
        '1.0.0-alpha.1',
        '1.0.0-alpha.beta',
        '1.0.0-beta',
        '1.0.0-beta.2',
        '1.0.0-beta.11',
        '1.0.0-rc.1',
        '1.0.0',
      ].map(SemanticVersion.parse).toList();

      for (var index = 0; index < versions.length - 1; index += 1) {
        expect(
          versions[index].compareTo(versions[index + 1]),
          lessThan(0),
          reason: '${versions[index]} must precede ${versions[index + 1]}',
        );
      }
    });

    test('compares arbitrarily long numeric prerelease identifiers', () {
      final lower = SemanticVersion.parse(
        '1.0.0-999999999999999999999999999999',
      );
      final higher = SemanticVersion.parse(
        '1.0.0-1000000000000000000000000000000',
      );

      expect(lower.compareTo(higher), lessThan(0));
      expect(
        SemanticVersion.parse(
          '1.0.0-10',
        ).compareTo(SemanticVersion.parse('1.0.0-alpha')),
        lessThan(0),
      );
    });

    test('ignores build metadata for precedence but not identity', () {
      final first = SemanticVersion.parse('1.2.3-rc.1+build.1');
      final second = SemanticVersion.parse('1.2.3-rc.1+build.2');

      expect(first.compareTo(second), 0);
      expect(first, isNot(second));
      expect({first, second}, hasLength(2));
      expect(
        SemanticVersion.parse('1.2.3+build-with-hyphen').isPrerelease,
        isFalse,
      );
    });

    test('enforces SemVer core and identifier syntax', () {
      for (final value in [
        '1',
        '1.0',
        '01.0.0',
        '1.01.0',
        '1.0.01',
        '1.0.0-',
        '1.0.0+',
        '1.0.0-01',
        '1.0.0-alpha.01',
        '1.0.0-alpha..1',
        '1.0.0+build..1',
        '1.0.0-alpha_1',
        '1.0.0+build_1',
        '1.0.0-alpha+build+again',
        'v1.0.0',
        ' 1.0.0',
        '1.0.0 ',
      ]) {
        expect(
          () => SemanticVersion.parse(value),
          throwsFormatException,
          reason: value,
        );
        expect(SemanticVersion.tryParse(value), isNull, reason: value);
      }

      for (final value in ['0.0.0-0', '1.0.0-alpha.01a', '1.0.0+001']) {
        expect(SemanticVersion.parse(value).toString(), value, reason: value);
      }
    });

    test('supports unbounded core numeric components', () {
      final huge = List.filled(256, '9').join();
      final larger = '1${List.filled(256, '0').join()}';
      final version = SemanticVersion.parse('$huge.$huge.$huge');

      expect(version.majorDigits, huge);
      expect(version.minorDigits, huge);
      expect(version.patchDigits, huge);
      expect(version.toString(), '$huge.$huge.$huge');
      expect(
        version.compareTo(SemanticVersion.parse('$larger.0.0')),
        lessThan(0),
      );
    });

    test('applies unbounded components inside version ranges', () {
      final huge = List.filled(128, '9').join();
      final next = '1${List.filled(128, '0').join()}';
      final exact = VersionRange.parse('>=$huge.0.0 <$next.0.0');
      final wildcard = VersionRange.parse('$huge.x');

      expect(exact.allows('$huge.999.999'), isTrue);
      expect(exact.allows('$next.0.0'), isFalse);
      expect(wildcard.min.toString(), '$huge.0.0');
      expect(wildcard.max.toString(), '$next.0.0');
      expect(wildcard.allows('$huge.42.7'), isTrue);
      expect(wildcard.allows('$next.0.0'), isFalse);
      expect(vpmRangeAllows('^$huge.0.0', '$huge.42.7'), isTrue);
      expect(vpmRangeAllows('^$huge.0.0', '$next.0.0'), isFalse);

      expect(() => VersionRange.parse('1.x.2'), throwsFormatException);
    });
  });

  group('VersionRange.parse', () {
    test('requires complete SemVer bounds while retaining wildcards', () {
      for (final value in ['1', '1.2', '>=1 <2.1']) {
        expect(
          () => VersionRange.parse(value),
          throwsFormatException,
          reason: value,
        );
      }

      expect(VersionRange.parse('1.x').allows('1.9.9'), isTrue);
      expect(VersionRange.parse('1.2.x').allows('1.2.9'), isTrue);
    });

    test('uses prerelease precedence and ignores build metadata', () {
      final prerelease = VersionRange.parse('>=1.0.0-alpha.2 <1.0.0');
      expect(prerelease.allows('1.0.0-alpha.1'), isFalse);
      expect(prerelease.allows('1.0.0-alpha.10'), isTrue);
      expect(prerelease.allows('1.0.0'), isFalse);

      final build = VersionRange.parse('=1.0.0+build.1');
      expect(build.allows('1.0.0+build.2'), isTrue);
    });

    test('round-trips normalized bounds with metadata', () {
      const source = '>=1.2.3-alpha.1+ci.7 <2.0.0-rc.1';
      final range = VersionRange.parse(source);

      expect(range.toString(), source);
      expect(VersionRange.parse(range.toString()).toString(), source);
    });

    test('rejects leading zeros and malformed metadata in ranges', () {
      for (final value in [
        '01.x',
        '1.01.x',
        '>=01',
        '>=1.0.0-01',
        '1.0.0+build_1',
        '>=1.0.0-alpha..1',
        '1..x',
      ]) {
        expect(
          () => VersionRange.parse(value),
          throwsFormatException,
          reason: value,
        );
      }
    });

    test('rejects text not consumed by comparator tokens', () {
      for (final value in [
        '>=1.0.0 garbage',
        'garbage >=1.0.0',
        '>=1.0.0 <2.0.0 trailing',
        '>=1.0.0<2.0.0',
      ]) {
        expect(
          () => VersionRange.parse(value),
          throwsFormatException,
          reason: value,
        );
      }
    });

    test('keeps the inclusivity of the strongest lower bound', () {
      final exclusive = VersionRange.parse('>2.0.0 >=1.0.0');
      expect(exclusive.allows('2.0.0'), isFalse);
      expect(exclusive.allows('2.0.1'), isTrue);

      final inclusive = VersionRange.parse('>=2.0.0 >1.0.0');
      expect(inclusive.allows('2.0.0'), isTrue);

      final equalBounds = VersionRange.parse('>=2.0.0 >2.0.0');
      expect(equalBounds.allows('2.0.0'), isFalse);
    });

    test('keeps the inclusivity of the strongest upper bound', () {
      final inclusive = VersionRange.parse('<=2.0.0 <3.0.0');
      expect(inclusive.allows('2.0.0'), isTrue);

      final exclusive = VersionRange.parse('<2.0.0 <=3.0.0');
      expect(exclusive.allows('2.0.0'), isFalse);

      final equalBounds = VersionRange.parse('<=2.0.0 <2.0.0');
      expect(equalBounds.allows('2.0.0'), isFalse);
    });

    test('intersects exact constraints and rejects empty ranges', () {
      final exact = VersionRange.parse('>=1.0.0 =2.0.0 <3.0.0');
      expect(exact.toString(), '2.0.0');

      for (final value in [
        '>2.0.0 <=2.0.0',
        '>=2.0.0 <2.0.0',
        '=1.0.0 =2.0.0',
      ]) {
        expect(
          () => VersionRange.parse(value),
          throwsFormatException,
          reason: value,
        );
      }
    });
  });
}
