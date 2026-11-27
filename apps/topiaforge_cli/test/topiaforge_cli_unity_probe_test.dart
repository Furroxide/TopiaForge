import 'dart:io';

import 'package:path/path.dart' as p;
import 'package:test/test.dart';

void main() {
  late Directory cliTemp;
  late File cliKernel;

  setUpAll(() async {
    if (Platform.isWindows) return;
    cliTemp = Directory.systemTemp.createTempSync('unity-probe-cli-');
    addTearDown(() {
      if (cliTemp.existsSync()) cliTemp.deleteSync(recursive: true);
    });
    cliKernel = File(p.join(cliTemp.path, 'topiaforge.dill'));
    // Compile once outside the probe budget: `dart run` includes cold CLI
    // compilation, which competes with other test processes on CI.
    final compiled = await Process.run(Platform.resolvedExecutable, [
      'compile',
      'kernel',
      'bin/topiaforge.dart',
      '-o',
      cliKernel.path,
    ], workingDirectory: Directory.current.path);
    expect(
      compiled.exitCode,
      0,
      reason: '${compiled.stdout}\n${compiled.stderr}',
    );
  });

  test(
    'UI bundle Unity probe times out and terminates a hung executable',
    () async {
      final temp = Directory.systemTemp.createTempSync('unity-probe-timeout-');
      final editor = File(p.join(temp.path, 'Unity'));
      final pidFile = File('${editor.path}.pid');
      int? childPid;
      addTearDown(() async {
        if (childPid != null) {
          await Process.run('kill', ['-9', '$childPid']);
        }
        if (temp.existsSync()) {
          temp.deleteSync(recursive: true);
        }
      });
      editor.writeAsStringSync('''#!/bin/sh
echo \$\$ > "\$0.pid"
exec /bin/sleep 300
''');
      final chmod = await Process.run('chmod', ['+x', editor.path]);
      expect(chmod.exitCode, 0, reason: chmod.stderr.toString());

      final stopwatch = Stopwatch()..start();
      final result = await Process.run(Platform.resolvedExecutable, [
        cliKernel.path,
        'unity',
        'build-ui-bundle',
        '--unity',
        editor.path,
        '--dry-run',
      ], workingDirectory: Directory.current.path);
      stopwatch.stop();

      expect(result.exitCode, 1);
      expect(result.stderr.toString(), contains('timed out'));
      expect(result.stderr.toString(), contains('was terminated'));
      expect(stopwatch.elapsed, lessThan(const Duration(seconds: 10)));
      childPid = int.parse(pidFile.readAsStringSync().trim());
      final stillRunning = await Process.run('kill', ['-0', '$childPid']);
      expect(stillRunning.exitCode, isNot(0));
      childPid = null;
    },
    skip: Platform.isWindows ? 'Uses POSIX process signals.' : false,
    timeout: const Timeout(Duration(seconds: 15)),
  );

  test(
    'UI bundle Unity probe bounds output and surfaces overflow',
    () async {
      final temp = Directory.systemTemp.createTempSync('unity-probe-output-');
      addTearDown(() {
        if (temp.existsSync()) temp.deleteSync(recursive: true);
      });
      final editor = File(p.join(temp.path, 'Unity'));
      editor.writeAsStringSync('''#!/bin/sh
while :; do
  printf 'xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx'
done
''');
      final chmod = await Process.run('chmod', ['+x', editor.path]);
      expect(chmod.exitCode, 0, reason: chmod.stderr.toString());

      final stopwatch = Stopwatch()..start();
      final result = await Process.run(Platform.resolvedExecutable, [
        cliKernel.path,
        'unity',
        'build-ui-bundle',
        '--unity',
        editor.path,
        '--dry-run',
      ], workingDirectory: Directory.current.path);
      stopwatch.stop();

      expect(result.exitCode, 1);
      expect(result.stderr.toString(), contains('combined output limit'));
      expect(result.stderr.toString(), contains('was terminated'));
      expect(stopwatch.elapsed, lessThan(const Duration(seconds: 10)));
    },
    skip: Platform.isWindows ? 'Uses a POSIX shell fixture.' : false,
    timeout: const Timeout(Duration(seconds: 15)),
  );
}
