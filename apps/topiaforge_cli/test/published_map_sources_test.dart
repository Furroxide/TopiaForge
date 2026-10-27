import 'dart:convert';
import 'dart:io';
import 'package:path/path.dart' as p;
import 'package:test/test.dart';

void main() {
  late Directory root;
  setUp(() => root = Directory.systemTemp.createTempSync('source-cli-'));
  tearDown(() => root.deleteSync(recursive: true));
  Future<ProcessResult> cli(List<String> args) => Process.run(
    Platform.resolvedExecutable,
    [
      p.absolute('bin/topiaforge.dart'),
      'world',
      'source',
      ...args,
      '--data-root',
      root.path,
    ],
    stdoutEncoding: utf8,
    stderrEncoding: utf8,
  );
  test(
    'CLI adds, lists, replaces and removes a source without network',
    () async {
      var result = await cli([
        'add',
        'https://robotopia.gg/map/example/',
        '--scene',
        'courtyard',
        '--name',
        'Courtyard',
      ]);
      expect(result.exitCode, 0, reason: '${result.stdout} ${result.stderr}');
      expect(result.stdout, contains('next game start'));
      result = await cli(['list']);
      expect(result.exitCode, 0, reason: '${result.stdout} ${result.stderr}');
      expect(result.stdout, contains('Courtyard · example · courtyard'));
      expect(result.stdout, isNot(contains(root.path)));
      expect(
        (await cli([
          'add',
          'example',
          '--scene',
          'courtyard',
          '--name',
          'New name',
        ])).exitCode,
        0,
      );
      result = await cli(['list']);
      expect(result.stdout, contains('New name'));
      expect(result.stdout, isNot(contains('Courtyard')));
      expect(
        (await cli(['remove', 'example', '--scene', 'courtyard'])).exitCode,
        0,
      );
      expect((await cli(['list'])).stdout, contains('No saved maps'));
    },
    // Six Dart CLI startups share this budget when suites run concurrently.
    timeout: const Timeout(Duration(minutes: 2)),
  );
  // Each cold startup keeps the default 30-second test budget and its own root.
  for (final entry in {
    'live documents': [
      'add',
      'automerge:document',
      '--scene',
      'scene',
      '--name',
      'Map',
    ],
    'wrong hosts': [
      'add',
      'https://example.test/map/map',
      '--scene',
      'scene',
      '--name',
      'Map',
    ],
    'missing explicit scene': ['add', 'example', '--name', 'Map'],
  }.entries) {
    test(
      'CLI rejects ${entry.key}',
      () async {
        final result = await cli(entry.value);
        expect(result.exitCode, isNot(0));
        expect(result.stderr, isNot(contains(root.path)));
      },
    );
  }
}
