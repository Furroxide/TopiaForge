import 'dart:convert';
import 'dart:io';

import 'package:launcher_data/src/launch_staging_store.dart';
import 'package:launcher_data/src/launch_storage_keys.dart';
import 'package:launcher_domain/launcher_domain.dart';
import 'package:path/path.dart' as p;
import 'package:test/test.dart';

void main() {
  late Directory root;
  late Directory game;
  late Directory staging;
  late LaunchStagingStore store;
  ProfileLaunchConfigurationV4 command(String requestId) =>
      ProfileLaunchConfigurationV4(
        profileId: 'profile',
        profileRevision: 1,
        requestId: requestId,
        command: 'main-menu',
        safeMode: false,
        inheritManagerModState: false,
        enabledMods: const [],
        selectedVersions: const {},
        packages: const [],
      );
  File channel(String prefix, String id) => File(
    p.join(staging.path, '$prefix-${LaunchStorageKeys.request(id)}.json'),
  );
  setUp(() {
    root = Directory(
      Directory.systemTemp
          .createTempSync('topiaforge-staging-')
          .resolveSymbolicLinksSync(),
    );
    game = Directory(p.join(root.path, 'game'))..createSync();
    staging = Directory(p.join(game.path, 'BepInEx', 'TopiaForge', 'staging'))
      ..createSync(recursive: true);
    store = LaunchStagingStore(game.path);
  });
  tearDown(() {
    if (root.existsSync()) root.deleteSync(recursive: true);
  });

  test(
    'request is atomically written under its portable identity and consumed alone',
    () async {
      final foreign = channel('launch-profile', 'other')
        ..writeAsStringSync(jsonEncode(command('other').toJson()));
      final written = await store.writeRequest(command('mine'));
      expect(written.path, channel('launch-profile', 'mine').path);
      expect(
        ProfileLaunchConfigurationV4.fromJson(
          jsonDecode(written.readAsStringSync()),
        ).requestId,
        'mine',
      );
      await store.deleteRequest(command('mine'));
      expect(written.existsSync(), isFalse);
      expect(foreign.existsSync(), isTrue);
      expect(staging.listSync().map((entry) => p.basename(entry.path)), [
        p.basename(foreign.path),
      ]);
    },
  );

  test('mismatched request cleanup preserves the foreign contents', () async {
    final file = channel('launch-profile', 'mine')
      ..writeAsStringSync(jsonEncode(command('other').toJson()));
    await store.deleteRequest(command('mine'));
    expect(file.existsSync(), isTrue);
    expect(
      ProfileLaunchConfigurationV4.fromJson(
        jsonDecode(file.readAsStringSync()),
      ).requestId,
      'other',
    );
  });

  test(
    'launch acknowledgement and session terminal remain separate channels',
    () async {
      final launch = LaunchOutcome(
        kind: 'launch',
        requestId: 'run',
        command: 'launch-target',
        sequence: 4,
        status: 'succeeded',
        phase: 'running',
        sessionId: 'session',
      );
      final terminal = LaunchOutcome(
        kind: 'session',
        requestId: 'run',
        sequence: 6,
        status: 'succeeded',
        phase: 'idle',
        sessionId: 'session',
      );
      channel(
        'launch-outcome',
        'run',
      ).writeAsStringSync(jsonEncode(launch.toJson()));
      channel(
        'session-outcome',
        'run',
      ).writeAsStringSync(jsonEncode(terminal.toJson()));
      expect(
        (await store.readOutcome('run', session: false))?.phase,
        'running',
      );
      expect((await store.readOutcome('run', session: true))?.phase, 'idle');
      expect(await store.readOutcome('other', session: false), isNull);
    },
  );

  test(
    'strict progress rejects foreign, partial and oversized files without deleting them',
    () async {
      final file = channel('launch-progress', 'mine');
      file.writeAsStringSync(
        jsonEncode(
          LaunchProgress(
            requestId: 'mine',
            sequence: 3,
            phase: 'preparing',
          ).toJson(),
        ),
      );
      expect((await store.readProgress('mine'))?.sequence, 3);
      file.writeAsStringSync(
        jsonEncode(
          LaunchProgress(
            requestId: 'other',
            sequence: 4,
            phase: 'running',
          ).toJson(),
        ),
      );
      expect(await store.readProgress('mine'), isNull);
      file.writeAsStringSync('{"schemaVersion":1,');
      expect(await store.readProgress('mine'), isNull);
      final handle = file.openSync(mode: FileMode.write);
      handle.truncateSync(4 * 1024 * 1024 + 1);
      handle.closeSync();
      expect(await store.readProgress('mine'), isNull);
      expect(file.existsSync(), isTrue);
    },
  );

  test('only correctly named observations become candidates', () async {
    final observation = LaunchObservationEnvelope(
      profileId: 'profile',
      profileRevision: 1,
      producer: PackageIdentity(id: 'example.worlds', version: '1.0.0'),
      packageSetDigest: '0123456789abcdef',
      observationRevision: 5,
    );
    final valid = File(
      p.join(
        staging.path,
        'runtime-observation-${LaunchStorageKeys.observation(observation)}.json',
      ),
    );
    valid.writeAsStringSync(jsonEncode(observation.toJson()));
    File(
      p.join(staging.path, 'runtime-observation-forged.json'),
    ).writeAsStringSync(jsonEncode(observation.toJson()));
    File(
      '${valid.path}.tmp',
    ).writeAsStringSync(jsonEncode(observation.toJson()));
    final values = await store.readObservations();
    expect(values.length, 1);
    expect(values.single.observationRevision, 5);
    expect(staging.listSync().length, 3);
  });

  test(
    'staging directory links cannot read, write or delete foreign files',
    () async {
      staging.deleteSync();
      final outside = Directory(p.join(root.path, 'foreign'))..createSync();
      final link = Link(staging.path);
      try {
        link.createSync(outside.path);
      } on FileSystemException catch (error) {
        markTestSkipped('Directory-link creation unavailable: $error');
        return;
      }
      addTearDown(() {
        if (link.existsSync()) link.deleteSync();
      });
      final request = File(
        p.join(
          outside.path,
          p.basename(channel('launch-profile', 'mine').path),
        ),
      )..writeAsStringSync(jsonEncode(command('mine').toJson()));
      final progress =
          File(
            p.join(
              outside.path,
              p.basename(channel('launch-progress', 'mine').path),
            ),
          )..writeAsStringSync(
            jsonEncode(
              LaunchProgress(
                requestId: 'mine',
                sequence: 1,
                phase: 'preparing',
              ).toJson(),
            ),
          );
      await expectLater(
        store.writeRequest(command('new')),
        throwsA(isA<FileSystemException>()),
      );
      expect(await store.readProgress('mine'), isNull);
      await store.deleteRequest(command('mine'));
      expect(request.existsSync(), isTrue);
      expect(progress.existsSync(), isTrue);
      expect(outside.listSync().length, 2);
    },
  );
}
