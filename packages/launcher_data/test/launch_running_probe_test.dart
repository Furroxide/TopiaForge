import 'dart:io';
import 'dart:convert';
import 'package:path/path.dart' as p;
import 'package:launcher_data/src/bounded_process.dart';
import 'package:launcher_data/src/launch_running_probe.dart';
import 'package:test/test.dart';

void main() {
  test(
    'Windows candidate aliases use the same canonical image identity',
    () async {
      const canonical = r'C:\actual\Robotopia.exe';
      const alias = r'C:\alias\Robotopia.exe';
      for (final (target, candidate) in [
        (alias, canonical),
        (canonical, alias),
      ]) {
        final probe = await probeWindowsLaunchProcessRunning(
          target,
          runProbe: (_, _) async => BoundedProcessResult(
            exitCode: 0,
            stdout: jsonEncode({
              'schemaVersion': 1,
              'paths': [candidate],
            }),
            stderr: '',
          ),
          resolveImage: (value) => value == alias ? canonical : value,
        );
        expect(probe, isTrue);
      }
    },
  );
  test(
    'Windows target stays data, and unexpected probe output stays unknown',
    () async {
      const target = r"C:\Game'; Write-Output injected; #\Robotopia.exe";
      final result = await probeWindowsLaunchProcessRunning(
        target,
        runProbe: (arguments, environment) async {
          expect(arguments, isNot(contains(target)));
          expect(arguments, contains('-EncodedCommand'));
          expect(environment['TOPIAFORGE_PROCESS_PROBE_TARGET'], target);
          return const BoundedProcessResult(
            exitCode: 2,
            stdout: '',
            stderr: 'partial failure',
          );
        },
      );
      expect(result, isNull);
      expect(
        await probeWindowsLaunchProcessRunning(
          target,
          runProbe: (_, _) async =>
              throw const ProcessException('probe', [], 'unavailable'),
        ),
        isNull,
      );
    },
  );
  group('Linux running-process admission', () {
    late Directory root;
    late String target;
    setUp(() async {
      root = await Directory.systemTemp.createTemp('topiaforge proc snapshot ');
      target = p.join(root.path, 'game', 'Robotopia.exe');
    });
    tearDown(() => root.delete(recursive: true));
    Future<File> commandLine(int processId) async {
      final directory = Directory(p.join(root.path, '$processId'));
      await directory.create();
      return File(p.join(directory.path, 'cmdline'));
    }

    // Another account's process shows its arguments, but Linux refuses every
    // other account its exe and cwd links.
    Future<String> refused(String path) async =>
        throw FileSystemException('Permission denied', path);
    Future<bool?> probeAnotherAccount() => probeLinuxLaunchProcessRunning(
      target,
      procRoot: root,
      readLink: refused,
    );

    test('missing proc snapshot remains unknown', () async {
      expect(
        await probeLinuxLaunchProcessRunning(
          target,
          procRoot: Directory(p.join(root.path, 'missing')),
        ),
        isNull,
      );
    });
    test('present unreadable process remains unknown', () async {
      // Neither its arguments nor its links can be read.
      await commandLine(2147480001);
      expect(await probeAnotherAccount(), isNull);
    });
    test('oversized and malformed process arguments remain unknown', () async {
      final file = await commandLine(2147480002);
      await file.writeAsBytes([0xff]);
      expect(
        await probeLinuxLaunchProcessRunning(target, procRoot: root),
        isNull,
      );
      await file.writeAsBytes(List.filled(1024 * 1024 + 1, 65));
      expect(
        await probeLinuxLaunchProcessRunning(target, procRoot: root),
        isNull,
      );
    });
    test('relative argv cannot hide a native game image', () async {
      final file = await commandLine(2147480004);
      await file.writeAsBytes(utf8.encode('./Robotopia.exe\u0000'));
      expect(
        await probeLinuxLaunchProcessRunning(
          target,
          procRoot: root,
          readLink: (path) async =>
              path.endsWith('exe') ? target : p.dirname(target),
        ),
        isTrue,
      );
    });
    test(
      'relative Wine game target resolves against its process cwd',
      () async {
        final file = await commandLine(2147480005);
        await file.writeAsBytes(utf8.encode('wine\u0000./Robotopia.exe\u0000'));
        expect(
          await probeLinuxLaunchProcessRunning(
            target,
            procRoot: root,
            readLink: (path) async => path.endsWith('exe')
                ? p.join(root.path, 'wine')
                : p.dirname(target),
          ),
          isTrue,
        );
      },
    );
    test(
      'empty snapshot is absent and complete exact arguments are running',
      () async {
        expect(
          await probeLinuxLaunchProcessRunning(target, procRoot: root),
          isFalse,
        );
        final file = await commandLine(2147480003);
        await file.writeAsBytes(
          utf8.encode('/usr/bin/wine\u0000$target\u0000'),
        );
        expect(
          await probeLinuxLaunchProcessRunning(target, procRoot: root),
          isTrue,
        );
        await file.writeAsBytes(utf8.encode('/another/Robotopia.exe\u0000'));
        expect(
          await probeLinuxLaunchProcessRunning(
            target,
            procRoot: root,
            readLink: (_) async => p.join(root.path, 'other', 'Robotopia.exe'),
          ),
          isFalse,
        );
      },
    );
    test(
      "root's PID 1, whose arguments name no game, is not the game",
      () async {
        final file = await commandLine(1);
        await file.writeAsBytes(utf8.encode('/sbin/init\u0000splash\u0000'));
        expect(await probeAnotherAccount(), isFalse);
      },
    );
    test(
      'kernel threads and zombies, which show no arguments, are not the game',
      () async {
        // Even root cannot read their exe links.
        await (await commandLine(2)).writeAsBytes(const []);
        await (await commandLine(2147480006)).writeAsBytes(const []);
        expect(await probeAnotherAccount(), isFalse);
      },
    );
    test(
      "another account's process naming this install's game by an absolute "
      'path is running',
      () async {
        final file = await commandLine(2147480007);
        await file.writeAsBytes(utf8.encode('wine\u0000$target\u0000'));
        expect(await probeAnotherAccount(), isTrue);
        await File(target).create(recursive: true);
        final alias = await Link(
          p.join(root.path, 'alias'),
        ).create(p.dirname(target));
        await file.writeAsBytes(
          utf8.encode('wine\u0000${p.join(alias.path, 'Robotopia.exe')}\u0000'),
        );
        expect(await probeAnotherAccount(), isTrue);
      },
      skip: Platform.isWindows
          ? 'Windows symlink creation needs privilege.'
          : false,
    );
    test(
      "another account's process naming another install's game is not the game",
      () async {
        final file = await commandLine(2147480008);
        await file.writeAsBytes(
          utf8.encode('wine\u0000/elsewhere/Robotopia.exe\u0000'),
        );
        expect(await probeAnotherAccount(), isFalse);
      },
    );
    test("another account's process naming the game by a relative path remains "
        'unknown', () async {
      final file = await commandLine(2147480009);
      await file.writeAsBytes(utf8.encode('wine\u0000./Robotopia.exe\u0000'));
      expect(await probeAnotherAccount(), isNull);
    });
    test("another account's process naming the game by a Windows path remains "
        'unknown', () async {
      final file = await commandLine(2147480010);
      await file.writeAsBytes(
        utf8.encode('wine\u0000Z:\\games\\Robotopia.exe\u0000'),
      );
      expect(await probeAnotherAccount(), isNull);
    }, skip: Platform.isWindows ? 'The Linux probe reads POSIX paths.' : false);
    test(
      'a process gone before its exe link is read is skipped',
      () async {
        final file = await commandLine(2147480011);
        await file.writeAsBytes(
          utf8.encode('wine\u0000Z:\\games\\Robotopia.exe\u0000'),
        );
        expect(
          await probeLinuxLaunchProcessRunning(
            target,
            procRoot: root,
            readLink: (path) async {
              await file.parent.delete(recursive: true);
              throw FileSystemException('No such file or directory', path);
            },
          ),
          isFalse,
        );
      },
      skip: Platform.isWindows ? 'The Linux probe reads POSIX paths.' : false,
    );
  });
  group('Windows running-process admission', () {
    test(
      'CIM failure remains unknown instead of confirming no process',
      () async {
        final result = await _probe('Write-Error "Access denied"');
        expect(result.exitCode, 3);
      },
    );
    test('unreadable matching image remains unknown', () async {
      final result = await _probe(
        r'[pscustomobject]@{ ExecutablePath = $null }',
      );
      expect(result.exitCode, 3);
    });
    test('only a complete empty snapshot establishes absence', () async {
      final result = await _probe('return @()');
      expect(result.exitCode, 0);
      expect(jsonDecode(result.stdout), {'schemaVersion': 1, 'paths': []});
    });
    test('another install is not mistaken for this install', () async {
      final result = await _probe(
        "[pscustomobject]@{ ExecutablePath = 'C:\\Other Game\\Robotopia.exe' }",
      );
      expect(result.exitCode, 0);
      expect(
        await probeWindowsLaunchProcessRunning(
          r'C:\Test Game\Robotopia.exe',
          runProbe: (_, _) async => result,
          resolveImage: (value) => value,
        ),
        isFalse,
      );
    });
    test('matching full image path is running', () async {
      final result = await _probe(
        "[pscustomobject]@{ ExecutablePath = 'c:\\test game\\Robotopia.exe' }",
      );
      expect(result.exitCode, 0);
    });
    test('a progress record from loading the CIM module still establishes '
        'absence', () async {
      // Windows PowerShell reports the module that Get-CimInstance loads as
      // a progress record, and under -EncodedCommand it writes that to a
      // redirected stderr. Any stderr makes the answer unknown, and an
      // unknown answer refuses every launch as Busy. This runs the product's
      // own flags and script, with a Get-CimInstance that reports progress
      // as loading its module does.
      var stderr = '';
      final answer = await probeWindowsLaunchProcessRunning(
        r'C:\Test Game\Robotopia.exe',
        runProbe: (arguments, environment) async {
          final encoded = base64Decode(arguments.last);
          final script = String.fromCharCodes([
            for (var index = 0; index + 1 < encoded.length; index += 2)
              encoded[index] | encoded[index + 1] << 8,
          ]);
          final mocked =
              'function Get-CimInstance { [CmdletBinding()] '
              'param(\$ClassName, \$Filter) Write-Progress -Activity '
              "'Preparing modules for first use.' -Completed; return @() }\n"
              '& {\n$script\n}\n';
          final result = await runBoundedProcess(
            '${Platform.environment['SystemRoot']}\\System32\\WindowsPowerShell\\v1.0\\powershell.exe',
            [
              ...arguments.take(arguments.length - 1),
              base64Encode(
                mocked.codeUnits
                    .expand((unit) => [unit & 255, unit >> 8])
                    .toList(),
              ),
            ],
            environment: environment,
            timeout: const Duration(seconds: 10),
            maxStdoutBytes: 4096,
            maxStderrBytes: 4096,
          );
          stderr = result.stderr;
          return result;
        },
        resolveImage: (value) => value,
      );
      expect(stderr, isEmpty);
      expect(answer, isFalse);
    });
  }, skip: !Platform.isWindows);
}

Future<BoundedProcessResult> _probe(String mock) async {
  final root = await Directory.systemTemp.createTemp('topiaforge probe test ');
  try {
    final script = File('${root.path}/probe.ps1');
    await script.writeAsString(
      'function Get-CimInstance { [CmdletBinding()] param(\$ClassName, \$Filter) '
      '$mock }\n& {\n$windowsLaunchRunningProbeScript\n} '
      "'C:\\Test Game\\Robotopia.exe'\n",
    );
    return await runBoundedProcess(
      '${Platform.environment['SystemRoot']}\\System32\\WindowsPowerShell\\v1.0\\powershell.exe',
      [
        '-NoProfile',
        '-NonInteractive',
        '-ExecutionPolicy',
        'Bypass',
        '-File',
        script.path,
      ],
      timeout: const Duration(seconds: 10),
      maxStdoutBytes: 4096,
      maxStderrBytes: 4096,
    );
  } finally {
    await root.delete(recursive: true);
  }
}
