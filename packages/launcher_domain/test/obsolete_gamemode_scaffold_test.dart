import 'package:launcher_domain/launcher_domain.dart';
import 'package:test/test.dart';

void main() {
  test('obsolete gamemode overrides are rejected without mutating input', () {
    final manifest = <String, Object?>{
      'schemaVersion': 6,
      'description': 'Original',
      'contributions': <String, Object?>{},
    };
    const options = ModScaffoldOptions(
      description: 'Changed',
      gamemodes: [GamemodeDefinition(id: 'sample.mode.round', name: 'Round')],
    );
    expect(
      () => options.applyTo(manifest),
      throwsA(
        isA<ArgumentError>().having(
          (error) => error.message.toString(),
          'repair guidance',
          allOf(
            contains('--template gamemode'),
            contains('contributions.gamemodes'),
          ),
        ),
      ),
    );
    expect(manifest, {
      'schemaVersion': 6,
      'description': 'Original',
      'contributions': <String, Object?>{},
    });
  });
}
