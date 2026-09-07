import 'dart:convert';
import 'dart:io';

import 'package:launcher_data/src/launch_activity_monitor.dart';
import 'package:launcher_data/src/launch_staging_store.dart';
import 'package:launcher_data/src/launch_storage_keys.dart';
import 'package:launcher_domain/launcher_domain.dart';
import 'package:path/path.dart' as p;
import 'package:test/test.dart';

void main() {
  late Directory root;
  late Directory staging;
  late LaunchActivityMonitor monitor;
  late List<LaunchActivity> updates;
  late DateTime now;
  late Duration elapsed;
  late void Function()? duringProbe;
  late bool? processAlive;
  void write(String channel, Map<String, Object?> value) => File(
    p.join(
      staging.path,
      '$channel-${LaunchStorageKeys.request('request')}.json',
    ),
  ).writeAsStringSync(jsonEncode(value));
  setUp(() {
    root = Directory(
      Directory.systemTemp
          .createTempSync('topiaforge-monitor-')
          .resolveSymbolicLinksSync(),
    );
    staging = Directory(p.join(root.path, 'BepInEx', 'TopiaForge', 'staging'))
      ..createSync(recursive: true);
    updates = [];
    now = DateTime.utc(2026);
    elapsed = Duration.zero;
    processAlive = true;
    duringProbe = null;
    monitor = LaunchActivityMonitor(
      store: LaunchStagingStore(root.path),
      current: LaunchActivity(
        requestId: 'request',
        profileId: 'profile',
        profileRevision: 3,
        installIdentity: root.path,
        packageDigest: '0123456789abcdef',
        command: 'launch-target',
        process: LaunchProcessIdentity(
          pid: 123,
          startTimeUtc: now,
          executablePath: p.join(root.path, 'game.exe'),
        ),
      ),
      onChanged: updates.add,
      processAlive: (_) async {
        duringProbe?.call();
        return processAlive;
      },
      elapsed: () => elapsed,
      acknowledgementTimeout: const Duration(seconds: 1),
    );
  });
  tearDown(() async {
    await monitor.dispose();
    if (root.existsSync()) root.deleteSync(recursive: true);
  });

  test(
    'process start and missing acknowledgement never imply Running',
    () async {
      await monitor.pollOnce();
      now = now.subtract(const Duration(days: 5));
      elapsed = const Duration(seconds: 2);
      await monitor.pollOnce();
      expect(monitor.current.unconfirmed, isTrue);
      expect(monitor.current.acknowledged, isFalse);
      expect(monitor.current.sessionStarted, isFalse);
      expect(monitor.current.launchOutcome, isNull);
    },
  );

  test(
    'fast acknowledgement and terminal outcome survive separate records',
    () async {
      write(
        'launch-outcome',
        LaunchOutcome(
          kind: 'launch',
          requestId: 'request',
          command: 'launch-target',
          sessionId: 'session',
          sequence: 4,
          phase: 'running',
          status: 'succeeded',
        ).toJson(),
      );
      write(
        'session-outcome',
        LaunchOutcome(
          kind: 'session',
          requestId: 'request',
          sessionId: 'session',
          sequence: 6,
          phase: 'idle',
          status: 'failed',
          error: LaunchOperationError(
            code: 'external',
            message: 'cleanup failed',
          ),
        ).toJson(),
      );
      await monitor.pollOnce();
      expect(monitor.current.sessionStarted, isTrue);
      expect(monitor.current.launchOutcome?.status, 'succeeded');
      expect(monitor.current.sessionOutcome?.error?.message, 'cleanup failed');
      expect(updates, isNotEmpty);
    },
  );

  test(
    'runtime launch failure cannot become success from internal menu fallback',
    () async {
      write(
        'launch-outcome',
        LaunchOutcome(
          kind: 'launch',
          requestId: 'request',
          command: 'launch-target',
          sequence: 2,
          phase: 'idle',
          status: 'failed',
          error: LaunchOperationError(
            code: 'invalidState',
            message: 'Selected provider failed verification',
          ),
        ).toJson(),
      );
      await monitor.pollOnce();
      expect(monitor.current.launchOutcome?.status, 'failed');
      expect(monitor.current.sessionStarted, isFalse);
      expect(monitor.current.launchOutcome?.command, 'launch-target');
      write(
        'launch-outcome',
        LaunchOutcome(
          kind: 'launch',
          requestId: 'request',
          command: 'main-menu',
          sequence: 3,
          phase: 'idle',
          status: 'succeeded',
        ).toJson(),
      );
      await monitor.pollOnce();
      expect(monitor.current.launchOutcome?.status, 'failed');
      expect(monitor.current.sessionStarted, isFalse);
    },
  );

  test(
    'foreign and out-of-order records do not replace current progress',
    () async {
      write(
        'launch-progress',
        LaunchProgress(
          requestId: 'request',
          sequence: 3,
          phase: 'starting-mode',
          sessionId: 'session',
        ).toJson(),
      );
      await monitor.pollOnce();
      write(
        'launch-progress',
        LaunchProgress(
          requestId: 'foreign',
          sequence: 100,
          phase: 'running',
          sessionId: 'other',
        ).toJson(),
      );
      await monitor.pollOnce();
      write(
        'launch-progress',
        LaunchProgress(
          requestId: 'request',
          sequence: 2,
          phase: 'loading-world',
          sessionId: 'session',
        ).toJson(),
      );
      await monitor.pollOnce();
      expect(monitor.current.progress?.sequence, 3);
      expect(monitor.current.progress?.phase, 'starting-mode');
    },
  );

  test(
    'verified process exit preserves successful acknowledgement without inventing session outcome',
    () async {
      write(
        'launch-outcome',
        LaunchOutcome(
          kind: 'launch',
          requestId: 'request',
          command: 'launch-target',
          sessionId: 'session',
          sequence: 4,
          phase: 'running',
          status: 'succeeded',
        ).toJson(),
      );
      await monitor.pollOnce();
      processAlive = false;
      await monitor.pollOnce();
      expect(monitor.current.processExited, isTrue);
      expect(monitor.current.launchOutcome?.status, 'succeeded');
      expect(monitor.current.sessionOutcome, isNull);
      expect(monitor.current.unconfirmed, isTrue);
    },
  );

  test(
    'exit probe does not lose final evidence written after the first read',
    () async {
      duringProbe = () {
        write(
          'launch-outcome',
          LaunchOutcome(
            kind: 'launch',
            requestId: 'request',
            command: 'launch-target',
            sessionId: 'session',
            sequence: 4,
            phase: 'running',
            status: 'succeeded',
          ).toJson(),
        );
        write(
          'session-outcome',
          LaunchOutcome(
            kind: 'session',
            requestId: 'request',
            sessionId: 'session',
            sequence: 6,
            phase: 'idle',
            status: 'succeeded',
          ).toJson(),
        );
        processAlive = false;
      };
      await monitor.pollOnce();
      expect(monitor.current.processExited, isTrue);
      expect(monitor.current.launchOutcome?.status, 'succeeded');
      expect(monitor.current.sessionOutcome?.status, 'succeeded');
      expect(monitor.current.unconfirmed, isFalse);
    },
  );

  test(
    'terminal-first evidence continues until the delayed launch acknowledgement',
    () async {
      write(
        'session-outcome',
        LaunchOutcome(
          kind: 'session',
          requestId: 'request',
          sessionId: 'session',
          sequence: 6,
          phase: 'idle',
          status: 'succeeded',
        ).toJson(),
      );
      await monitor.pollOnce();
      expect(monitor.finished, isFalse);
      write(
        'launch-outcome',
        LaunchOutcome(
          kind: 'launch',
          requestId: 'request',
          command: 'launch-target',
          sessionId: 'session',
          sequence: 4,
          phase: 'running',
          status: 'succeeded',
        ).toJson(),
      );
      await monitor.pollOnce();
      expect(monitor.current.sessionStarted, isTrue);
      expect(monitor.finished, isTrue);
    },
  );

  test(
    'disposed monitor cannot publish later filesystem observations',
    () async {
      await monitor.dispose();
      write(
        'launch-progress',
        LaunchProgress(
          requestId: 'request',
          sequence: 1,
          phase: 'preparing',
        ).toJson(),
      );
      await monitor.pollOnce();
      expect(updates, isEmpty);
    },
  );
}
