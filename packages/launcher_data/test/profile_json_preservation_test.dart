import 'dart:convert';
import 'dart:io';

import 'package:launcher_data/launcher_data.dart';
import 'package:path/path.dart' as p;
import 'package:test/test.dart';

void main() {
  late Directory root;
  late LocalLauncherRepository repository;
  setUp(() {
    root = Directory.systemTemp.createTempSync('topiaforge-profile-json-');
    repository = LocalLauncherRepository(
      dataRoot: p.join(root.path, 'data'),
      repositoryRoot: root.path,
    );
  });
  tearDown(() async {
    await repository.dispose();
    root.deleteSync(recursive: true);
  });
  const profile =
      '{"id":"legacy","name":"Legacy","worldSelection":{"future":NUMBER}}';

  for (final kind in ['store', 'import']) {
    test('$kind refuses fractional schema version without writing', () async {
      final rawProfile =
          '{"id":"current","name":"Current","revision":0,"launchSelection":{"schemaVersion":1,"kind":"main-menu"}}';
      final key = kind == 'store' ? 'profiles' : 'profile';
      final value = kind == 'store' ? '[$rawProfile]' : rawProfile;
      final file = File(
        p.join(
          repository.dataRoot,
          kind == 'store' ? 'profiles.json' : 'input.topiaforgeprofile.json',
        ),
      );
      file.parent.createSync(recursive: true);
      final text = '{"schemaVersion":3.0,"$key":$value}';
      file.writeAsStringSync(text);
      await expectLater(
        kind == 'store'
            ? repository.loadSnapshot()
            : repository.importProfile(file.path),
        throwsFormatException,
      );
      expect(file.readAsStringSync(), text);
    });
    for (final token in [
      '1234567890123456789012345678901234567890',
      '1.0000000000000001',
      '1e-999',
    ]) {
      test(
        '$kind refuses numeric value $token before automatic migration',
        () async {
          final rawProfile = profile.replaceFirst('NUMBER', token);
          final key = kind == 'store' ? 'profiles' : 'profile';
          final value = kind == 'store' ? '[$rawProfile]' : rawProfile;
          final file = File(
            p.join(
              repository.dataRoot,
              kind == 'store'
                  ? 'profiles.json'
                  : 'input.topiaforgeprofile.json',
            ),
          );
          file.parent.createSync(recursive: true);
          final text = '{"schemaVersion":2,"$key":$value}';
          file.writeAsStringSync(text);
          await expectLater(
            kind == 'store'
                ? repository.loadSnapshot()
                : repository.importProfile(file.path),
            throwsA(
              isA<FormatException>().having(
                (error) => error.message,
                'actionable preservation refusal',
                contains('without changing its value'),
              ),
            ),
          );
          expect(file.readAsStringSync(), text);
          expect(
            file.parent.listSync().where(
              (entry) =>
                  entry.path.endsWith('.bak') || entry.path.endsWith('.tmp'),
            ),
            isEmpty,
          );
        },
      );
    }
  }

  test(
    'escaped-equivalent duplicate profile properties cannot be migrated',
    () async {
      final file = File(p.join(repository.dataRoot, 'profiles.json'));
      file.parent.createSync(recursive: true);
      const text =
          r'{"schemaVersion":2,"profiles":[{"id":"legacy","name":"Legacy","worldSelection":{"value":1,"\u0076alue":2}}]}';
      file.writeAsStringSync(text);
      await expectLater(
        repository.loadSnapshot(),
        throwsA(
          isA<FormatException>().having(
            (error) => error.message,
            'duplicate refusal',
            contains('duplicate property'),
          ),
        ),
      );
      expect(file.readAsStringSync(), text);
    },
  );

  test(
    'safe numeric equivalents and number-like strings preserve values',
    () async {
      final file = File(p.join(root.path, 'input.topiaforgeprofile.json'))
        ..writeAsStringSync(
          '{"schemaVersion":2,"profile":{"id":"legacy","name":"Legacy","worldSelection":{"values":[1.0,1e0,0.1,9223372036854775807],"text":"1234567890123456789012345678901234567890"}}}',
        );
      final imported = await repository.importProfile(file.path);
      await repository.exportProfile(imported, file.path);
      final result = await repository.importProfile(file.path);
      expect(result.launchSelection.legacy, imported.launchSelection.legacy);
      expect(
        jsonEncode(result.launchSelection.legacy),
        contains('9223372036854775807'),
      );
    },
  );
}
