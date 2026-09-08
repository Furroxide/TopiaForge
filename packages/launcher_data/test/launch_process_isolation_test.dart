import 'dart:async';
import 'dart:convert';
import 'dart:io';
import 'package:launcher_data/launcher_data.dart';
import 'package:launcher_domain/launcher_domain.dart';
import 'package:path/path.dart' as p;
import 'package:test/test.dart';

void main() {
  group('Original process isolation', () {
    late Directory root;
    late File report;
    late File script;
    final owned = <LaunchProcessIdentity>[];
    setUp(() {
      root = Directory.systemTemp.createTempSync('tf-owned-isolation-');
      report = File(p.join(root.path, 'report.json'));
      script = File(p.join(root.path, 'child.dart'))
        ..writeAsStringSync(r'''
import 'dart:async'; import 'dart:convert'; import 'dart:io';
Future<void> main(List<String> args) async {
  File(args.first).writeAsStringSync(jsonEncode({'pid': pid,
    'explicit': Platform.environment['ONLY_THIS'],
    'ambientPath': Platform.environment.containsKey('PATH'),
    'ambientProfile': Platform.environment.containsKey('USERPROFILE')}));
  await Future<void>.delayed(const Duration(seconds: 15));
}
''');
    });
    tearDown(() async {
      for (final identity in owned) {
        if (await isLaunchProcessAlive(identity) == true) {
          await stopLaunchProcess(identity);
        }
        expect(await isLaunchProcessAlive(identity), isFalse);
      }
      owned.clear();
      root.deleteSync(recursive: true);
    });
    Future<Map<String, Object?>> waitReport() async {
      final watch = Stopwatch()..start();
      while (watch.elapsed < const Duration(seconds: 8)) {
        try {
          return jsonDecode(report.readAsStringSync()) as Map<String, Object?>;
        } on FileSystemException {
          /* owned child not ready */
        } on FormatException {
          /* bounded report write in progress */
        }
        await Future<void>.delayed(const Duration(milliseconds: 20));
      }
      throw TimeoutException('Synthetic owned child did not report.');
    }

    test(
      'OS identity is measured without reading environment identity values',
      () {
        final identity = readWindowsAcceptanceIdentity();
        expect(identity.userSid.startsWith('S-1-'), isTrue);
        expect(RegExp(r'^[0-9a-f]{16}$').hasMatch(identity.logonId), isTrue);
        expect(identity.sessionId, greaterThanOrEqualTo(0));
        expect(p.isAbsolute(identity.userProfile), isTrue);
        expect(p.isAbsolute(identity.localAppDataLow), isTrue);
        expect(identity.matches(readWindowsAcceptanceIdentity()), isTrue);
      },
    );
    test(
      'exact child token is verified before resume with no inherited environment',
      () async {
        final receipt = await startLaunchProcessWithReceipt(
          executable: childExecutable,
          arguments: [script.path, report.path],
          workingDirectory: root.path,
          environment: {'ONLY_THIS': 'controlled'},
          inheritParentEnvironment: false,
          requiredWindowsIdentity: readWindowsAcceptanceIdentity(),
        );
        owned.add(receipt.identity!);
        final result = await waitReport();
        expect(result, {
          'pid': receipt.pid,
          'explicit': 'controlled',
          'ambientPath': false,
          'ambientProfile': false,
        });
        expect(await isLaunchProcessAlive(receipt.identity!), isTrue);
      },
    );
    test(
      'wrong OS generation or inherited environment refuses before child writes',
      () async {
        final actual = readWindowsAcceptanceIdentity();
        final wrong = WindowsAcceptanceIdentity(
          userSid: actual.userSid,
          logonId: actual.logonId == '0000000000000000'
              ? '0000000000000001'
              : '0000000000000000',
          sessionId: actual.sessionId,
          userProfile: actual.userProfile,
          localAppDataLow: actual.localAppDataLow,
        );
        for (final value in [(wrong, false), (actual, true)]) {
          await expectLater(
            startLaunchProcessWithReceipt(
              executable: childExecutable,
              arguments: [script.path, report.path],
              workingDirectory: root.path,
              environment: {},
              inheritParentEnvironment: value.$2,
              requiredWindowsIdentity: value.$1,
            ),
            throwsStateError,
          );
          expect(report.existsSync(), isFalse);
        }
      },
    );
  }, skip: !Platform.isWindows);
}

String get childExecutable {
  final vm = p.join(p.dirname(Platform.resolvedExecutable), 'dartvm.exe');
  return File(vm).existsSync() ? vm : Platform.resolvedExecutable;
}
