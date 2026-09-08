import 'package:launcher_domain/launcher_domain.dart';
import 'package:test/test.dart';

void main() {
  for (final field in ['worldGamemodes', 'gamemodes']) {
    for (final value in <Object?>[
      null,
      [],
      [
        {'id': 'sample.mode.round', 'name': 'Round'},
      ],
    ]) {
      test(
        'retired $field=$value template input is refused without mutation',
        () {
          final manifest = <String, Object?>{
            'schemaVersion': 6,
            'description': 'Original',
            field: value,
          };
          expect(
            () => const ModScaffoldOptions(
              description: 'Changed',
            ).applyTo(manifest),
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
            field: value,
          });
        },
      );
    }
  }
}
