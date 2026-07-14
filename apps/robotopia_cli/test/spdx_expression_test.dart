import 'package:robotopia/src/spdx_expression.dart';
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
}
