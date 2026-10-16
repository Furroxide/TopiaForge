import 'dart:io';

import 'package:topiaforge/src/release_policy.dart';
import 'package:topiaforge/src/release_spdx_third_party.dart';
import 'package:topiaforge/src/spdx_expression.dart';
import 'package:test/test.dart';

void main() {
  test(
    'accepts SPDX 3.28 identifiers, references, exceptions, and legacy +',
    () {
      for (final expression in [
        'MIT',
        'GPL-2.0+',
        'GPL-2.0-only WITH Classpath-exception-2.0',
        '(MIT OR Apache-2.0) AND LicenseRef-Commercial',
        'DocumentRef-vendor:LicenseRef-Custom',
      ]) {
        expect(
          SpdxExpressionValidator.validate(expression),
          isNull,
          reason: expression,
        );
      }
    },
  );

  test('rejects unknown licenses and exception identifiers', () {
    expect(
      SpdxExpressionValidator.validate('Definitely-Not-A-License'),
      contains('unknown SPDX license'),
    );
    expect(
      SpdxExpressionValidator.validate('MIT WITH Made-Up-Exception'),
      contains('unknown SPDX exception'),
    );
  });

  test('NOASSERTION is opt-in and never publishable by default', () {
    expect(
      SpdxExpressionValidator.validate('NOASSERTION'),
      contains('placeholder'),
    );
    expect(
      SpdxExpressionValidator.validate('NOASSERTION', allowNoAssertion: true),
      isNull,
    );
  });

  test('every licence the release SBOM declares is an explicit SPDX 3.28 '
      'expression', () {
    final root = Directory.current.parent.parent.path;
    final policy = TopiaForgeReleasePolicy.load(root);
    final inventory = ReleaseSpdxThirdPartyInventory.load(
      repositoryRoot: root,
      policy: policy,
      release: TopiaForgeReleaseCatalog.load(
        root,
      ).release(policy.productVersion),
    );
    final licences = {
      for (final entry in inventory.packages) entry.licenseDeclared,
      for (final entry in releaseVendoredComponents.values) entry.license,
      ...releaseSpdxRecordedLicenses.values,
    };
    expect(licences, contains('LGPL-2.1-only'));
    for (final licence in licences) {
      expect(
        SpdxExpressionValidator.validate(licence, allowNoAssertion: true),
        isNull,
        reason: licence,
      );
      // A bare GNU identifier leaves "only" versus "or later" undecided.
      expect(licence, isNot(matches(RegExp(r'^(A|L)?GPL-[0-9.]+\+?$'))));
    }
  });
}
