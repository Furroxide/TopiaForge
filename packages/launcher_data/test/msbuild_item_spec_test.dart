import 'package:launcher_data/src/linear_text_scan.dart';
import 'package:launcher_data/src/msbuild_item_spec.dart';
import 'package:test/test.dart';

import 'scan_agreement.dart';

const _abstractions = 'TopiaForge.Mods.Abstractions';
const _robotKit = 'TopiaForge.Mods.RobotKit';

/// How the [attribute] of a `PackageReference` written with [value] names
/// [package].
PackageNaming _naming(
  String attribute,
  String value, {
  String package = _abstractions,
}) {
  final tag = '<PackageReference $attribute="$value" />';
  return packageNaming(tag, readStartTag(tag, 0)!, attribute, package);
}

// The references, escapes, lists, wildcards, expressions, cases and white
// space below were each checked with `dotnet msbuild -getItem`, or with a
// restore against an empty feed, on an item after the package's own
// `<PackageReference Include="id" />`.
void main() {
  group('packageNaming', () {
    test('reads one id however MSBuild spells it', () {
      for (final value in const [
        _abstractions,
        ' topiaforge.mods.abstractions ',
        '$_abstractions;',
        ';$_abstractions;;$_abstractions',
        // A character reference, and MSBuild's escape for `s`.
        'TopiaForge.Mods.Abstraction&#115;',
        'TopiaForge.Mods.Abstraction&#x73;',
        'TopiaForge.Mods.Abstraction%73',
        '&#9;$_abstractions&#10;',
        '&#160;$_abstractions',
      ]) {
        for (final attribute in const ['Include', 'Update', 'Remove']) {
          expect(
            _naming(attribute, value),
            PackageNaming.alone,
            reason: '$attribute="$value"',
          );
        }
      }
    });

    test('reads a list, a wildcard or an expression as shared', () {
      for (final value in const [
        '$_abstractions;Other',
        ' Other ; topiaforge.mods.abstractions ',
        '$_abstractions&#59;Other',
        // MSBuild reads a reference that XML forbids as a character too.
        '$_abstractions;&#1;',
        r'$(Extra);TopiaForge.Mods.Abstractions',
      ]) {
        for (final attribute in const ['Include', 'Update', 'Remove']) {
          expect(
            _naming(attribute, value),
            PackageNaming.shared,
            reason: '$attribute="$value"',
          );
        }
      }
      // A wildcard matches the items before an Update or a Remove, in any
      // case. In an Include it names files instead.
      for (final value in const [
        'TopiaForge.Mods.*',
        'topiaforge.mods.*',
        'Topia*Abstractions',
        'TopiaForge.Mods.Abstraction?',
        'TopiaForge.Mods.&#42;',
        'TopiaForge.Mods.Abstraction%73*',
        '*',
        '**',
        'Other;TopiaForge.*',
      ]) {
        for (final attribute in const ['Update', 'Remove']) {
          expect(
            _naming(attribute, value),
            PackageNaming.shared,
            reason: '$attribute="$value"',
          );
        }
        expect(
          _naming('Include', value),
          PackageNaming.none,
          reason: 'Include="$value"',
        );
      }
    });

    test('reads no name where MSBuild reads another', () {
      for (final value in const [
        'TopiaForge.Mods',
        'TopiaForge-Mods-Abstractions',
        // Escaped, a `;`, `*` or `?` is a plain character.
        '$_abstractions%3BOther',
        'TopiaForge.Mods.%2A',
        'TopiaForge.Mods.Abstraction%3F',
        '$_abstractions%zz',
        // A path separator is no wildcard match.
        'TopiaForge/*',
        // A character MSBuild keeps, even one XML forbids, is part of the id.
        'TopiaForge.Mods.Abstraction&#0;',
        'TopiaForge.Mods.Abstractions&#xD800;',
        // MSBuild cannot load these: MSB4025 for the references, and
        // MSB4035 for an empty value.
        'TopiaForge.Mods.Abstraction&s;',
        'TopiaForge.Mods.Abstractions&',
        '',
      ]) {
        for (final attribute in const ['Include', 'Update', 'Remove']) {
          expect(
            _naming(attribute, value),
            PackageNaming.none,
            reason: '$attribute="$value"',
          );
        }
      }
    });

    test('trims white space as .NET does, and an included id again', () {
      // MSBuild trims each part where char.IsWhiteSpace holds, which takes
      // U+0085 and leaves U+FEFF and U+180E, unlike `\s`. NuGet trims an
      // included id again once MSBuild has unescaped it, while MSBuild
      // matches an Update or Remove by the unescaped value.
      const alone = PackageNaming.alone;
      const none = PackageNaming.none;
      for (final (value, include, update) in [
        ('&#x85;$_abstractions&#x85;', alone, alone),
        ('${String.fromCharCode(0x85)}$_abstractions', alone, alone),
        ('&#x3000;$_abstractions&#x2028;', alone, alone),
        (
          'Other;&#x85;$_abstractions',
          PackageNaming.shared,
          PackageNaming.shared,
        ),
        ('&#xFEFF;$_abstractions', none, none),
        ('$_abstractions&#xFEFF;', none, none),
        ('&#x180E;$_abstractions', none, none),
        ('%20$_abstractions', alone, none),
        ('$_abstractions%85', alone, none),
        ('%A0$_abstractions%09', alone, none),
      ]) {
        expect(_naming('Include', value), include, reason: 'Include="$value"');
        for (final attribute in const ['Update', 'Remove']) {
          expect(
            _naming(attribute, value),
            update,
            reason: '$attribute="$value"',
          );
        }
      }
    });

    test('compares case as NuGet and MSBuild do', () {
      // An id compares ordinal ignoring case, which folds only the ASCII
      // letters of an ASCII id. NuGet reads each of these as another
      // package, and MSBuild applies no such Update to the item.
      for (final (value, package) in const [
        ('Top&#x131;aForge.Mods.Abstractions', _abstractions),
        ('TopiaForge.Mod&#x17F;.Abstractions', _abstractions),
        ('TOP&#x130;AFORGE.MODS.ABSTRACTIONS', _abstractions),
        ('TopiaForge.Mods.Robot&#x212A;it', _robotKit),
      ]) {
        for (final attribute in const ['Include', 'Update', 'Remove']) {
          expect(
            _naming(attribute, value, package: package),
            PackageNaming.none,
            reason: '$attribute="$value"',
          );
        }
      }
      // A wildcard ignores case as a .NET regular expression does, which
      // matches U+0130 to i and the Kelvin sign to k, but not U+0131 or
      // U+017F.
      for (final (value, package, naming) in const [
        ('TOP&#x130;AFORGE.*', _abstractions, PackageNaming.shared),
        ('TopiaForge.Mods.Robot&#x212A;*', _robotKit, PackageNaming.shared),
        ('Top&#x131;a*', _abstractions, PackageNaming.none),
        ('TopiaForge.Mod&#x17F;.*', _abstractions, PackageNaming.none),
      ]) {
        for (final attribute in const ['Update', 'Remove']) {
          expect(
            _naming(attribute, value, package: package),
            naming,
            reason: '$attribute="$value"',
          );
        }
      }
    });

    test('leaves an expression alone, which MSBuild evaluates first', () {
      // MSBuild reads the package through each of these, but what they
      // evaluate to depends on properties and items a scan cannot see.
      for (final value in const [
        r'$(SdkId)',
        '@(PackageReference)',
        "@(PackageReference->'%(Identity)')",
      ]) {
        for (final attribute in const ['Include', 'Update', 'Remove']) {
          expect(
            _naming(attribute, value),
            PackageNaming.none,
            reason: '$attribute="$value"',
          );
        }
      }
    });

    test('reads only the attribute named in its exact case', () {
      const tag =
          '<PackageReference update="$_abstractions" '
          'Update="Other" Include="$_abstractions" />';
      final read = readStartTag(tag, 0)!;
      expect(
        packageNaming(tag, read, 'Update', _abstractions),
        PackageNaming.none,
      );
      expect(
        packageNaming(tag, read, 'Include', _abstractions),
        PackageNaming.alone,
      );
    });

    test('finishes long values within the budget', () {
      for (final value in [
        ';' * 200000,
        '*' * 200000,
        '?' * 200000,
        '${'*a' * 100000}x',
        '&#59;' * 40000,
        '&' * 200000,
        '&${'a' * 200000}',
        '&#${'0' * 200000}115;',
        '%' * 200000,
        '%73' * 70000,
        '\$(' * 70000,
        '$_abstractions;' * 13000,
      ]) {
        for (final attribute in const ['Include', 'Update']) {
          withinBudget(() => _naming(attribute, value));
        }
      }
    });
  });

  group('firstPackageReference', () {
    test('finds the first item that includes the package at all', () {
      const listed = '<PackageReference Include="Other;$_abstractions" />';
      const alone = '<PackageReference Include="$_abstractions" />';
      expect(firstPackageReference('$listed$alone', _abstractions), (
        start: 0,
        end: listed.length,
        shared: true,
      ));
      expect(firstPackageReference('$alone$listed', _abstractions), (
        start: 0,
        end: alone.length,
        shared: false,
      ));
      expect(
        firstPackageReference(
          '<PackageReference Include="TopiaForge.Mods.*" />',
          _abstractions,
        ),
        isNull,
      );
    });
  });
}
