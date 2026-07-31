import 'dart:io';

import 'package:path/path.dart' as p;
import 'package:test/test.dart';

void main() {
  test(
    'ugc go-live rejects a stale Automerge session before launch',
    () async {
      final packageRoot = Directory.current.absolute.path;
      final temp = Directory.systemTemp.createTempSync('ugc-go-live-test-');
      addTearDown(() {
        if (temp.existsSync()) {
          temp.deleteSync(recursive: true);
        }
      });
      final environment = {
        ...Platform.environment,
        'TOPIAFORGE_DATA_ROOT': p.join(temp.path, 'data'),
        'ROBOTOPIA_GAME_DIR': p.join(temp.path, 'missing-game'),
      };

      final created = await Process.run(
        Platform.resolvedExecutable,
        ['run', 'topiaforge', 'new', 'mod', 't.golive', '--dir', temp.path],
        workingDirectory: packageRoot,
        environment: environment,
      );
      expect(created.exitCode, 0, reason: '${created.stdout}\n${created.stderr}');
      final project = p.join(temp.path, 't.golive');
      final setup = await Process.run(
        Platform.resolvedExecutable,
        [
          p.join(packageRoot, 'bin', 'topiaforge.dart'),
          'ugc',
          'setup',
          '--transport',
          'automerge',
          '--no-deploy',
        ],
        workingDirectory: project,
        environment: environment,
      );
      expect(setup.exitCode, 0, reason: '${setup.stdout}\n${setup.stderr}');

      final session = File(
        p.join(environment['TOPIAFORGE_DATA_ROOT']!, 'ugc-session.json'),
      )..createSync(recursive: true);
      session.writeAsStringSync('''
{"documentUrl":"automerge:stale","publisherLeaseToken":"stale","publisherPid":2147483647}
''');

      final result = await Process.run(
        Platform.resolvedExecutable,
        [p.join(packageRoot, 'bin', 'topiaforge.dart'), 'ugc', 'go-live'],
        workingDirectory: project,
        environment: environment,
      );

      expect(result.exitCode, 1);
      expect(result.stderr.toString(), contains('No active Automerge publisher'));
      expect(result.stderr.toString(), isNot(contains('No Robotopia install')));
    },
    timeout: const Timeout(Duration(minutes: 1)),
  );
}
