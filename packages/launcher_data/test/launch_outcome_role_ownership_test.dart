import 'dart:convert';
import 'dart:io';

import 'package:launcher_data/src/launch_activity_monitor.dart';
import 'package:launcher_data/src/launch_staging_store.dart';
import 'package:launcher_data/src/launch_storage_keys.dart';
import 'package:launcher_domain/launcher_domain.dart';
import 'package:path/path.dart' as p;
import 'package:test/test.dart';

void main() {
  for (final role in LaunchSessionRole.values) {
    for (final acknowledged in [false, true]) {
      test(
        '$role refuses another role before/after ack: $acknowledged',
        () async {
          final root = Directory.systemTemp.createTempSync('outcome-role-');
          addTearDown(() => root.deleteSync(recursive: true));
          final staging = Directory(
            p.join(root.path, 'BepInEx', 'TopiaForge', 'staging'),
          )..createSync(recursive: true);
          final foreign = role == LaunchSessionRole.join
              ? LaunchSessionRole.host
              : LaunchSessionRole.join;
          final watcher = LaunchActivityMonitor(
            store: LaunchStagingStore(root.path),
            current: LaunchActivity(
              requestId: 'request',
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
          void write(String prefix, Object record) => File(
            p.join(
              staging.path,
              '$prefix-${LaunchStorageKeys.request('request')}.json',
            ),
          ).writeAsStringSync(jsonEncode(record));
          if (acknowledged) {
            write('launch-outcome', {
              'schemaVersion': 2,
              'kind': 'launch',
              'requestId': 'request',
              'sequence': 1,
              'command': role.command,
              'phase': 'idle',
              'status': 'succeeded',
              'blocks': [],
              'multiplayer': {'role': role.name, 'intentId': 'intent'},
            });
            await watcher.pollOnce();
            expect(watcher.current.launchOutcome, isNotNull);
          }
          Map<String, Object> terminal(LaunchSessionRole value) => {
            'schemaVersion': 2,
            'kind': 'session',
            'requestId': 'request',
            'sequence': 2,
            'phase': 'idle',
            'status': 'succeeded',
            'blocks': [],
            'multiplayer': {
              'role': value.name,
              'intentId': 'intent',
              'endReason': 'future-private-reason',
              'detail': 'Private provider detail.',
            },
          };
          write('session-outcome', terminal(foreign));
          await watcher.pollOnce();
          expect(watcher.current.sessionOutcome, isNull);
          expect(watcher.finished, isFalse);
          write('session-outcome', terminal(role));
          await watcher.pollOnce();
          expect(
            watcher.current.sessionOutcome!.multiplayer!.endReason,
            LaunchSessionEndReason.other,
          );
          expect(watcher.finished, acknowledged);
        },
      );
    }
  }
}
