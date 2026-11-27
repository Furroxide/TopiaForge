import 'package:launcher_domain/launcher_domain.dart';
import 'package:test/test.dart';

void main() {
  for (final version in [4, 5]) {
    test('retired V$version rejects before interpreting fields', () {
      for (final fields in [
        <String, Object?>{},
        {'name': 42, 'multiplayer': true},
      ]) {
        expect(
          () => ModManifest.fromJson({'schemaVersion': version, ...fields}),
          throwsA(
            isA<FormatException>().having(
              (error) => error.message,
              'actionable retirement',
              allOf(
                contains('retired'),
                contains('schemaVersion 6'),
                contains('topiaforge migrate-manifest'),
              ),
            ),
          ),
        );
      }
    });
    test('retired V$version model stops before content validation', () {
      final issues = ModManifest(
        schemaVersion: version,
        id: '',
        name: '',
        version: '',
      ).validate();
      expect(issues, hasLength(1));
      expect(issues.single.isBlocking, isTrue);
      expect(issues.single.message, contains('schemaVersion 6'));
      expect(issues.single.message, contains('topiaforge migrate-manifest'));
      expect(ModManifest.isSupportedSchemaVersion(version), isFalse);
    });
  }
}
