import 'dart:io';

import 'package:path/path.dart' as p;
import 'package:test/test.dart';

void main() {
  late Directory temp;
  final cli = p.join(Directory.current.path, 'bin', 'topiaforge.dart');

  setUp(() {
    temp = Directory.systemTemp.createTempSync('topiaforge-mod-usage-');
  });
  tearDown(() {
    temp.deleteSync(recursive: true);
  });

  Future<ProcessResult> run(List<String> arguments) => Process.run(
    Platform.resolvedExecutable,
    [cli, ...arguments],
    workingDirectory: temp.path,
    environment: {'TOPIAFORGE_DATA_ROOT': p.join(temp.path, 'data')},
  );

  test('mod usage guides gamemode authors to the active template', () async {
    final result = await run(['mod']);
    expect(result.exitCode, 0, reason: '${result.stdout} ${result.stderr}');
    final output = result.stdout as String;
    expect(output, isNot(contains('mod add|remove gamemode')));
    expect(output, contains('new mod <id> --template gamemode'));
    expect(output, contains('contributions.gamemodes'));
    expect(output, contains('contributions.launchTargets'));
    expect(output, contains('mod add|remove dependency|optional-dependency'));
  });

  for (final action in ['add', 'remove']) {
    test('mod $action gamemode refuses before accessing a project', () async {
      final project = Directory(p.join(temp.path, 'absent-project'));
      final result = await run([
        'mod',
        action,
        'gamemode',
        '--project',
        project.path,
      ]);
      expect(result.exitCode, isNot(0));
      final output = '${result.stdout} ${result.stderr}';
      expect(output, contains('gamemode editing is retired'));
      expect(output, contains('--template gamemode'));
      expect(output, contains('contributions.gamemodes'));
      expect(output, contains('contributions.launchTargets'));
      expect(project.existsSync(), isFalse);
    });
  }
}
