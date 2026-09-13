import 'dart:async';
import 'dart:convert';
import 'dart:io';
import 'package:path/path.dart' as p;
import 'package:test/test.dart';
import 'package:topiaforge/src/sandbox_acceptance/native_run_support.dart';

void main() {
  late Directory root;
  late Completer<int> exit;
  late bool exited;
  late int forced;
  late SandboxBrokerCancellation stop;
  setUp(() {
    root = Directory.systemTemp.createTempSync('sandbox-native-cancel-test-');
    exit = Completer<int>();
    exited = false;
    forced = 0;
    stop = SandboxBrokerCancellation(
      runRoot: root.path,
      challenge: 'a' * 64,
      exit: exit.future,
      isExited: () => exited,
      forceKill: () {
        forced++;
        exited = true;
        exit.complete(1);
      },
      gracePeriod: const Duration(milliseconds: 25),
      exitGrace: const Duration(milliseconds: 50),
    );
  });
  tearDown(() => root.deleteSync(recursive: true));
  test(
    'cooperative stop writes correlated request before waiting and never force-kills',
    () async {
      final pending = stop.request();
      final file = File(p.join(root.path, 'broker-cancel.json'));
      expect(jsonDecode(file.readAsStringSync()), {
        'schemaVersion': 1,
        'kind': 'sandbox-broker-cancel-v1',
        'challenge': 'a' * 64,
      });
      expect(forced, 0);
      exited = true;
      exit.complete(1);
      await pending;
      expect(forced, 0);
      expect(stop.forced, false);
    },
  );
  test(
    'repeated interruptions share the same cancellation ownership',
    () async {
      final first = stop.request();
      final second = stop.request();
      expect(identical(first, second), isTrue);
      exited = true;
      exit.complete(1);
      await Future.wait([first, second]);
      expect(forced, 0);
    },
  );
  test(
    'exited original process never receives cancellation or force termination',
    () async {
      exited = true;
      exit.complete(0);
      await stop.request();
      expect(
        File(p.join(root.path, 'broker-cancel.json')).existsSync(),
        isFalse,
      );
      expect(forced, 0);
    },
  );
  test(
    'expired cooperative grace terminates once and reports unconfirmed cleanup',
    () async {
      final pending = stop.request();
      expect(forced, 0);
      await expectLater(pending, throwsStateError);
      expect(forced, 1);
      expect(stop.forced, true);
      await expectLater(stop.request(), throwsStateError);
      expect(forced, 1);
    },
  );
  test(
    'foreign cancel record is preserved and never treated as acknowledged cleanup',
    () async {
      final file = File(p.join(root.path, 'broker-cancel.json'));
      const original = '{"unrelated":"record"}';
      file.writeAsStringSync(original);
      final pending = stop.request();
      exited = true;
      exit.complete(1);
      await expectLater(pending, throwsStateError);
      expect(file.readAsStringSync(), original);
      expect(forced, 0);
    },
  );
}
