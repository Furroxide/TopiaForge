import 'dart:async';
import 'dart:convert';
import 'dart:io';
import 'package:launcher_data/src/launch_process_control.dart';
import 'package:launcher_domain/launcher_domain.dart';
import 'package:path/path.dart' as p;
import 'package:test/test.dart';

void main() {
  group('Windows native launch receipts', () {
    late _CreationFixture fixture;
    setUp(() async => fixture = await _CreationFixture.create());
    tearDown(() => fixture.dispose());

    test(
      'creation returns original process identity with exact Unicode argv environment and cwd',
      () async {
        final arguments = [
          '',
          'space value',
          'quote"inside',
          r'end\',
          r'back\\"quote',
          'Unicode 🦊 café',
          '&|<>^%;',
        ];
        fixture.childMayExist = true;
        final receipt = await startLaunchProcessWithReceipt(
          executable: _childExecutable,
          arguments: [
            fixture.script.path,
            fixture.report.path,
            fixture.stop.path,
            ...arguments,
          ],
          workingDirectory: fixture.root.path,
          environment: {
            'TOPIAFORGE_CREATION_TEST': 'value with = and 🦊',
            'topiaforge_creation_case': 'discard first',
            'TOPIAFORGE_CREATION_CASE': 'last value',
          },
        );
        fixture.receipt = receipt;
        expect(
          receipt.identity,
          isNotNull,
          reason:
              'Detached PID alone cannot establish initial process ownership.',
        );
        final report = await fixture.waitForReport();
        expect(report['arguments'], arguments);
        expect(report['environment'], 'value with = and 🦊');
        expect(report['caseEnvironment'], 'last value');
        expect(
          p.normalize(report['cwd'] as String),
          p.normalize(fixture.root.path),
        );
        expect(report['pid'], receipt.pid);
        expect(await isLaunchProcessAlive(receipt.identity!), isTrue);
        expect(await stopLaunchProcess(receipt.identity!), isTrue);
        fixture.stopped = true;
      },
    );

    test(
      'created process outlives spawning helper without inherited stdio',
      () async {
        final helper = File(p.join(fixture.root.path, 'launch helper.dart'));
        await helper.writeAsString(r'''
import 'dart:convert'; import 'dart:io';
import 'package:launcher_data/src/launch_process_control.dart';
Future<void> main(List<String> args) async {
  final result = await startLaunchProcessWithReceipt(executable: args.first,
    arguments: args.skip(1).toList(), workingDirectory: Directory.current.path,
    environment: {'topiaforge_creation_case': 'overridden after merge'});
  final identity = result.identity;
  stdout.writeln(jsonEncode({'pid': result.pid, 'start': identity?.startTimeUtc.toIso8601String(),
    'image': identity?.executablePath, 'token': identity?.nativeStartToken}));
}
''');
        fixture.childMayExist = true;
        final result = await Process.run(
          Platform.resolvedExecutable,
          [
            '--packages=${p.join(Directory.current.path, '.dart_tool', 'package_config.json')}',
            helper.path,
            _childExecutable,
            fixture.script.path,
            fixture.report.path,
            fixture.stop.path,
          ],
          workingDirectory: fixture.root.path,
          environment: {
            'TOPIAFORGE_CREATION_CASE': 'inherited value',
            'TOPIAFORGE_CREATION_TEST': 'inherited unchanged',
          },
        ).timeout(const Duration(seconds: 15));
        expect(result.exitCode, 0, reason: result.stderr.toString());
        final receipt = jsonDecode(result.stdout.toString()) as Map;
        expect(receipt['start'], isNotNull);
        fixture.receipt = LaunchProcessReceipt(
          pid: receipt['pid'] as int,
          identity: LaunchProcessIdentity(
            pid: receipt['pid'] as int,
            startTimeUtc: DateTime.parse(receipt['start'] as String),
            executablePath: receipt['image'] as String,
            nativeStartToken: receipt['token'] as String,
          ),
        );
        final report = await fixture.waitForReport();
        expect(report['pid'], fixture.receipt!.pid);
        expect(report['caseEnvironment'], 'overridden after merge');
        expect(report['environment'], 'inherited unchanged');
        expect(await isLaunchProcessAlive(fixture.receipt!.identity!), isTrue);
      },
    );

    test(
      'invalid launch inputs refuse creation without child side effects',
      () async {
        final nul = String.fromCharCode(0);
        final invalid =
            <
              ({
                String? executable,
                String? cwd,
                List<String>? args,
                Map<String, String>? env,
              })
            >[
              (executable: 'dart.exe', cwd: null, args: null, env: null),
              (
                executable: '${Platform.resolvedExecutable}$nul',
                cwd: null,
                args: null,
                env: null,
              ),
              (executable: null, cwd: '.', args: null, env: null),
              (
                executable: null,
                cwd: '${fixture.root.path}$nul',
                args: null,
                env: null,
              ),
              (
                executable: null,
                cwd: null,
                args: ['bad${nul}argument'],
                env: null,
              ),
              (executable: null, cwd: null, args: ['x' * 32767], env: null),
              (executable: null, cwd: null, args: null, env: {'': 'value'}),
              (
                executable: null,
                cwd: null,
                args: null,
                env: {'BAD=KEY': 'value'},
              ),
              (
                executable: null,
                cwd: null,
                args: null,
                env: {'BAD${nul}KEY': 'value'},
              ),
              (
                executable: null,
                cwd: null,
                args: null,
                env: {'KEY': 'bad${nul}value'},
              ),
            ];
        final before = await fixture.root
            .list()
            .map((entry) => entry.path)
            .toList();
        for (final value in invalid) {
          await expectLater(
            startLaunchProcessWithReceipt(
              executable: value.executable ?? Platform.resolvedExecutable,
              arguments: [
                fixture.script.path,
                fixture.report.path,
                fixture.stop.path,
                ...?value.args,
              ],
              workingDirectory: value.cwd ?? fixture.root.path,
              environment: value.env ?? {},
            ).then((receipt) {
              fixture.receipt = receipt;
              fixture.childMayExist = true;
              return receipt;
            }),
            throwsArgumentError,
          );
        }
        expect(await fixture.report.exists(), isFalse);
        expect(
          await fixture.root.list().map((entry) => entry.path).toList(),
          before,
        );
      },
    );

    test(
      'missing native executable fails without creating directories or files',
      () async {
        await expectLater(
          startLaunchProcessWithReceipt(
            executable: p.join(
              fixture.root.path,
              'missing',
              'not installed.exe',
            ),
            arguments: [fixture.report.path],
            workingDirectory: fixture.root.path,
            environment: {},
          ),
          throwsA(isA<ProcessException>()),
        );
        expect(await fixture.report.exists(), isFalse);
        expect(
          await Directory(p.join(fixture.root.path, 'missing')).exists(),
          isFalse,
        );
      },
    );
  }, skip: !Platform.isWindows);
}

// New SDKs use dart.exe as a spawning CLI. Run the VM directly so the child
// reporting its PID is exactly the process created by CreateProcessW.
String get _childExecutable {
  final direct = p.join(p.dirname(Platform.resolvedExecutable), 'dartvm.exe');
  return File(direct).existsSync() ? direct : Platform.resolvedExecutable;
}

class _CreationFixture {
  _CreationFixture(this.root);
  final Directory root;
  File get script => File(p.join(root.path, 'owned child.dart'));
  File get report => File(p.join(root.path, 'child report.json'));
  File get stop => File(p.join(root.path, 'stop'));
  LaunchProcessReceipt? receipt;
  bool stopped = false;
  bool childMayExist = false;
  static Future<_CreationFixture> create() async {
    final root = await Directory.systemTemp.createTemp(
      'topiaforge native creation ',
    );
    final fixture = _CreationFixture(root);
    await fixture.script.writeAsString(r'''
import 'dart:async'; import 'dart:convert'; import 'dart:io';
Future<void> main(List<String> args) async {
  await File(args[0]).writeAsString(jsonEncode({'pid': pid, 'cwd': Directory.current.path,
    'environment': Platform.environment['TOPIAFORGE_CREATION_TEST'],
    'caseEnvironment': Platform.environment['TOPIAFORGE_CREATION_CASE'], 'arguments': args.skip(2).toList()}));
  final timeout = Stopwatch()..start();
  while (!File(args[1]).existsSync() && timeout.elapsed < const Duration(seconds: 20)) {
    await Future<void>.delayed(const Duration(milliseconds: 20));
  }
  await File('${args[1]}.exited').writeAsString('done');
  // A completion file is observable before Windows releases the process cwd.
  await Future<void>.delayed(const Duration(milliseconds: 300));
}
''');
    return fixture;
  }

  Future<Map> waitForReport() async {
    final elapsed = Stopwatch()..start();
    while (elapsed.elapsed < const Duration(seconds: 10)) {
      try {
        return jsonDecode(await report.readAsString()) as Map;
      } on FileSystemException {
        /* Child has not written its receipt yet. */
      } on FormatException {
        /* Child is still writing its small report. */
      }
      await Future<void>.delayed(const Duration(milliseconds: 20));
    }
    throw TimeoutException('Owned child did not report readiness.');
  }

  Future<void> dispose() async {
    if (childMayExist) {
      if (!stopped) await stop.writeAsString('stop');
      final identity = receipt?.identity;
      if (identity == null) {
        throw StateError(
          'The owned child has no verified exit identity; retain its fixture.',
        );
      }
      final elapsed = Stopwatch()..start();
      // Its last file write does not prove that Windows released the cwd yet.
      // Only the recorded process generation may satisfy this cleanup barrier.
      while (await isLaunchProcessAlive(identity) != false) {
        if (elapsed.elapsed >= const Duration(seconds: 12)) {
          throw TimeoutException(
            'The owned child did not confirm exit; retain its fixture.',
          );
        }
        await Future<void>.delayed(const Duration(milliseconds: 20));
      }
    }
    await root.delete(recursive: true);
  }
}
