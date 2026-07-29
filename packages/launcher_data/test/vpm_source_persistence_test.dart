import 'dart:convert';
import 'dart:io';

import 'package:launcher_data/launcher_data.dart';
import 'package:path/path.dart' as p;
import 'package:test/test.dart';

void main() {
  late Directory temp;
  late Directory dataRoot;
  late Directory repositoryRoot;

  setUp(() {
    temp = Directory.systemTemp.createTempSync('topiaforge-vpm-sources-');
    dataRoot = Directory(p.join(temp.path, 'data'))..createSync();
    repositoryRoot = Directory(p.join(temp.path, 'repository'))..createSync();
  });

  tearDown(() {
    if (temp.existsSync()) {
      temp.deleteSync(recursive: true);
    }
  });

  LocalDeveloperRepository repository() => LocalDeveloperRepository(
    dataRoot: dataRoot.path,
    repositoryRoot: repositoryRoot.path,
  );

  for (final formatVersion in <int?>[null, 1]) {
    final label = formatVersion == null ? 'missing' : 'version 1';
    test('VPM source store ignores $label formatVersion', () async {
      final payload = <String, Object?>{
        'sources': [
          {
            'id':
                'robo'
                'topia.vpm.custom',
            'name': 'Retired',
            'url': temp.path,
          },
        ],
      };
      if (formatVersion != null) {
        payload['formatVersion'] = formatVersion;
      }
      File(
        p.join(dataRoot.path, 'vpm_sources.json'),
      ).writeAsStringSync(jsonEncode(payload));

      final sources = await repository().listUnityRepos();

      expect(sources, hasLength(1));
      expect(sources.single.id, 'io.github.furroxide.topiaforge.vpm.local');
    });
  }

  test('VPM source store ignores retired ids at formatVersion 2', () async {
    File(p.join(dataRoot.path, 'vpm_sources.json')).writeAsStringSync(
      jsonEncode({
        'formatVersion': 2,
        'sources': [
          {
            'id':
                'robo'
                'topia.vpm.custom',
            'name': 'Retired',
            'url': temp.path,
          },
        ],
      }),
    );

    final sources = await repository().listUnityRepos();

    expect(sources, hasLength(1));
    expect(sources.single.id, 'io.github.furroxide.topiaforge.vpm.local');
  });

  test('VPM source store round-trips formatVersion 2', () async {
    final custom = Directory(p.join(temp.path, 'custom'))..createSync();
    final saved = await repository().addUnityRepo(custom.path, name: 'Custom');
    final stored =
        jsonDecode(
              File(
                p.join(dataRoot.path, 'vpm_sources.json'),
              ).readAsStringSync(),
            )
            as Map<String, Object?>;
    final restored = await repository().listUnityRepos();

    expect(stored['formatVersion'], 2);
    expect(saved.where((source) => !source.builtIn), hasLength(1));
    expect(restored.where((source) => !source.builtIn), hasLength(1));
    expect(restored.last.url, custom.path);
  });
}
