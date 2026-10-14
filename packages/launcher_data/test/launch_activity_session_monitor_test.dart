import 'dart:io';

import 'package:launcher_data/src/launch_activity_monitor.dart';
import 'package:launcher_data/src/launch_staging_store.dart';
import 'package:launcher_data/src/launch_storage_keys.dart';
import 'package:launcher_domain/launcher_domain.dart';
import 'package:path/path.dart' as p;
import 'package:test/test.dart';

/// A session launch (request V5) reads progress and outcomes V2 through the
/// same monitor, and owes a terminal record once its session started.
void main() {
  late Directory root;
  late Directory staging;
  late bool? processAlive;

  LaunchActivityMonitor monitor(String command) => LaunchActivityMonitor(
    store: LaunchStagingStore(root.path),
    current: LaunchActivity(
      requestId: 'request',
      profileId: 'mp-0123456789abcdef',
      profileRevision: 0,
      installIdentity: root.path,
      packageDigest: '0123456789abcdef',
      command: command,
      process: LaunchProcessIdentity(
        pid: 123,
        startTimeUtc: DateTime.utc(2026),
        executablePath: p.join(root.path, 'game.exe'),
      ),
    ),
    onChanged: (_) {},
    processAlive: (_) async => processAlive,
    elapsed: () => Duration.zero,
  );

  void write(String channel, String text) => File(
    p.join(
      staging.path,
      '$channel-${LaunchStorageKeys.request('request')}.json',
    ),
  ).writeAsStringSync(text);

  LaunchOutcomeMultiplayer join() => LaunchOutcomeMultiplayer(
    role: LaunchSessionRole.join,
    intentId: 'intent-1',
  );

  setUp(() {
    root = Directory(
      Directory.systemTemp
          .createTempSync('topiaforge-session-monitor-')
          .resolveSymbolicLinksSync(),
    );
    staging = Directory(p.join(root.path, 'BepInEx', 'TopiaForge', 'staging'))
      ..createSync(recursive: true);
    processAlive = true;
  });
  tearDown(() {
    if (root.existsSync()) root.deleteSync(recursive: true);
  });

  test('a join reads its V2 progress and success', () async {
    final watcher = monitor('join-session');
    addTearDown(watcher.dispose);
    write(
      'launch-progress',
      LaunchTransport.writeProgress(
        LaunchProgress(
          requestId: 'request',
          sequence: 2,
          phase: 'synchronizing',
          session: LaunchProgressSession(
            role: LaunchSessionRole.join,
            intentId: 'intent-1',
            state: LaunchSessionState.synchronizing,
          ),
        ),
      ),
    );
    await watcher.pollOnce();
    expect(watcher.current.progress!.phase, 'synchronizing');
    write(
      'launch-outcome',
      LaunchTransport.writeOutcome(
        LaunchOutcome(
          kind: 'launch',
          requestId: 'request',
          command: 'join-session',
          sessionId: 'session-1',
          sequence: 3,
          status: 'succeeded',
          phase: 'running',
          multiplayer: join(),
        ),
      ),
    );
    await watcher.pollOnce();
    expect(watcher.current.sessionStarted, isTrue);
    expect(watcher.finished, isFalse);

    processAlive = false;
    await watcher.pollOnce();
    expect(watcher.finished, isTrue);
    expect(watcher.current.unconfirmed, isTrue);
  });

  test('a join that ends with its terminal record is confirmed', () async {
    final watcher = monitor('join-session');
    addTearDown(watcher.dispose);
    write(
      'launch-outcome',
      LaunchTransport.writeOutcome(
        LaunchOutcome(
          kind: 'launch',
          requestId: 'request',
          command: 'join-session',
          sequence: 3,
          status: 'succeeded',
          phase: 'idle',
          multiplayer: join(),
        ),
      ),
    );
    write(
      'session-outcome',
      LaunchTransport.writeOutcome(
        LaunchOutcome(
          kind: 'session',
          requestId: 'request',
          sequence: 9,
          status: 'succeeded',
          phase: 'idle',
          multiplayer: LaunchOutcomeMultiplayer(
            role: LaunchSessionRole.join,
            intentId: 'intent-1',
            endReason: LaunchSessionEndReason.hostEnded,
          ),
        ),
      ),
    );
    await watcher.pollOnce();
    expect(watcher.finished, isTrue);
    expect(
      watcher.current.sessionOutcome!.multiplayer!.endReason!.joinEndReason,
      JoinSessionEndReason.hostEnded,
    );
    processAlive = false;
    await watcher.pollOnce();
    expect(watcher.current.unconfirmed, isFalse);
  });

  test('a failed join finishes with its failure', () async {
    final watcher = monitor('join-session');
    addTearDown(watcher.dispose);
    write(
      'launch-outcome',
      LaunchTransport.writeOutcome(
        LaunchOutcome(
          kind: 'launch',
          requestId: 'request',
          command: 'join-session',
          sequence: 4,
          status: 'failed',
          phase: 'connecting',
          multiplayer: LaunchOutcomeMultiplayer(
            role: LaunchSessionRole.join,
            intentId: 'intent-1',
            failure: LaunchMultiplayerFailure.passwordRejected,
          ),
        ),
      ),
    );
    await watcher.pollOnce();
    expect(watcher.finished, isTrue);
    expect(
      watcher.current.launchOutcome!.multiplayer!.failure!.joinFailureCode,
      JoinFailureCode.passwordRejected,
    );
  });
}
