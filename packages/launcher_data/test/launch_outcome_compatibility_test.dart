import 'dart:io';

import 'package:launcher_data/src/launch_activity_monitor.dart';
import 'package:launcher_data/src/launch_staging_store.dart';
import 'package:launcher_data/src/launch_storage_keys.dart';
import 'package:launcher_domain/launcher_domain.dart';
import 'package:path/path.dart' as p;
import 'package:test/test.dart';

import '../../launcher_domain/test/multiplayer_fixtures.dart';

void main() {
  final fixture = readMultiplayerFixture('progress-v2/forward-compat.json');
  late Directory root;
  late Directory staging;
  late LaunchStagingStore store;
  setUp(() {
    root = Directory.systemTemp.createTempSync('outcome-compat-');
    staging = Directory(p.join(root.path, 'BepInEx', 'TopiaForge', 'staging'))
      ..createSync(recursive: true);
    store = LaunchStagingStore(root.path);
  });
  tearDown(() => root.deleteSync(recursive: true));

  void write(String text, {bool terminal = false}) => File(
    p.join(
      staging.path,
      '${terminal ? 'session' : 'launch'}-outcome-'
      '${LaunchStorageKeys.request('compat-request')}.json',
    ),
  ).writeAsStringSync(text);

  for (final item in asObjects(fixture['valid'])) {
    test('${item['id']} reaches a confirmed terminal monitor result', () async {
      final expected = LaunchTransport.readOutcome(item['expected']! as String);
      final terminal = expected.kind == 'session';
      final role = expected.multiplayer!.role;
      final watcher = LaunchActivityMonitor(
        store: store,
        current: LaunchActivity(
          requestId: 'compat-request',
          profileId: 'profile',
          profileRevision: 0,
          installIdentity: root.path,
          packageDigest: '0123456789abcdef',
          command: role.command,
        ),
        onChanged: (_) {},
        elapsed: () => Duration.zero,
      );
      addTearDown(watcher.dispose);
      if (terminal) {
        write(
          LaunchTransport.writeOutcome(
            LaunchOutcome(
              kind: 'launch',
              requestId: 'compat-request',
              command: role.command,
              sequence: 2,
              status: 'succeeded',
              phase: 'idle',
              multiplayer: LaunchOutcomeMultiplayer(
                role: role,
                intentId: 'compat-intent',
              ),
            ),
          ),
        );
        await watcher.pollOnce();
        expect(watcher.finished, isFalse);
      }
      write(item['text']! as String, terminal: terminal);
      final read = await store.readOutcome('compat-request', session: terminal);
      expect(read, isNotNull);
      expect(LaunchTransport.writeOutcome(read!), item['expected']);
      await watcher.pollOnce();
      expect(watcher.finished, isTrue);
      expect(watcher.current.unconfirmed, isFalse);
      final result = terminal
          ? watcher.current.sessionOutcome
          : watcher.current.launchOutcome;
      expect(LaunchTransport.writeOutcome(result!), item['expected']);
      // Reading does not rewrite the producer's file.
      expect(
        File(
          p.join(
            staging.path,
            '${terminal ? 'session' : 'launch'}-outcome-'
            '${LaunchStorageKeys.request('compat-request')}.json',
          ),
        ).readAsStringSync(),
        item['text'],
      );
    });
  }
  for (final item in asObjects(fixture['invalid'])) {
    test('${item['id']} is refused by the production store', () async {
      for (final terminal in [false, true]) {
        write(item['text']! as String, terminal: terminal);
        expect(
          await store.readOutcome('compat-request', session: terminal),
          isNull,
        );
      }
    });
  }
  for (final role in ['join', 'host']) {
    test(
      'an unknown $role failure stays confirmed after process exit',
      () async {
        final watcher = LaunchActivityMonitor(
          store: store,
          current: LaunchActivity(
            requestId: 'compat-request',
            profileId: 'profile',
            profileRevision: 0,
            installIdentity: root.path,
            packageDigest: '0123456789abcdef',
            command: '$role-session',
            process: LaunchProcessIdentity(
              pid: 123,
              startTimeUtc: DateTime.utc(2026),
              executablePath: p.join(root.path, 'game.exe'),
            ),
          ),
          onChanged: (_) {},
          processAlive: (_) async => false,
          elapsed: () => Duration.zero,
        );
        addTearDown(watcher.dispose);
        write(
          asObjects(fixture['valid']).singleWhere(
                (item) => item['id'] == '$role-failure-world',
              )['text']!
              as String,
        );
        await watcher.pollOnce();
        expect(watcher.finished, isTrue);
        expect(watcher.current.processExited, isTrue);
        expect(watcher.current.unconfirmed, isFalse);
        expect(
          watcher.current.launchOutcome!.multiplayer!.failure,
          LaunchMultiplayerFailure.other,
        );
      },
    );
  }
  for (final field in ['requestId', 'intentId']) {
    test('unknown failure cannot replace another $field', () async {
      final watcher = LaunchActivityMonitor(
        store: store,
        current: LaunchActivity(
          requestId: 'compat-request',
          profileId: 'profile',
          profileRevision: 0,
          installIdentity: root.path,
          packageDigest: '0123456789abcdef',
          command: 'join-session',
          progress: LaunchProgress(
            requestId: 'compat-request',
            sequence: 1,
            phase: 'connecting',
            session: LaunchProgressSession(
              role: LaunchSessionRole.join,
              intentId: 'compat-intent',
              state: LaunchSessionState.connecting,
            ),
          ),
        ),
        onChanged: (_) {},
        elapsed: () => Duration.zero,
      );
      addTearDown(watcher.dispose);
      final text = asObjects(fixture['valid']).first['text']! as String;
      write(
        text.replaceAll(
          field == 'requestId' ? 'compat-request' : 'compat-intent',
          'foreign',
        ),
      );
      await watcher.pollOnce();
      expect(watcher.finished, isFalse);
      expect(watcher.current.launchOutcome, isNull);
      write(text);
      await watcher.pollOnce();
      expect(watcher.finished, isTrue);
    });
  }
  test(
    'a newer monitor ignores a late outcome for the older request',
    () async {
      final watcher = LaunchActivityMonitor(
        store: store,
        current: LaunchActivity(
          requestId: 'new-request',
          profileId: 'profile',
          profileRevision: 0,
          installIdentity: root.path,
          packageDigest: '0123456789abcdef',
          command: 'join-session',
        ),
        onChanged: (_) {},
        elapsed: () => Duration.zero,
      );
      addTearDown(watcher.dispose);
      write(asObjects(fixture['valid']).first['text']! as String);
      await watcher.pollOnce();
      expect(watcher.finished, isFalse);
      expect(watcher.current.launchOutcome, isNull);
    },
  );
}
