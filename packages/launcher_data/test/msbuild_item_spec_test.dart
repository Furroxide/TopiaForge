import 'package:launcher_data/src/linear_text_scan.dart';
import 'package:launcher_data/src/msbuild_item_spec.dart';
import 'package:test/test.dart';

import 'scan_agreement.dart';

const _abstractions = 'TopiaForge.Mods.Abstractions';

/// How the [attribute] of a `PackageReference` written with [value] names
/// the Abstractions package.
PackageNaming _naming(String attribute, String value) {
  final tag = '<PackageReference $attribute="$value" />';
  return packageNaming(tag, readStartTag(tag, 0)!, attribute, _abstractions);
}

// The references, escapes, lists, wildcards and expressions below were each
// checked with `dotnet msbuild -getItem`, on an item after
// `<PackageReference Include="TopiaForge.Mods.Abstractions" />`.
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
        '%20$_abstractions',
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
