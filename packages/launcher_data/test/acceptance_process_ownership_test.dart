import 'dart:async';
import 'dart:convert';
import 'dart:io';

import 'package:launcher_data/launcher_data.dart';
import 'package:launcher_domain/launcher_domain.dart';
import 'package:path/path.dart' as p;
import 'package:test/test.dart';

import 'acceptance_isolation_fixture.dart';

void main() {
  group('Repository acceptance receipt ownership', () {
    late _OwnershipFixture fixture;
    setUp(() async => fixture = await _OwnershipFixture.create());
    tearDown(() => fixture.dispose());

    test(
      'equivalent caller receipt uses the captured original for native control',
      () async {
        final original = fixture.launched.process!;
        final clone = fixture.copyResult();
        expect(clone.process, isNot(same(original)));
        await fixture.repository.stopAcceptanceProcess(clone);
        expect(fixture.stopped, [same(original)]);
        expect(fixture.probed, isNotEmpty);
        expect(fixture.probed, everyElement(same(original)));
        expect(fixture.lookups, 0);
      },
    );

    test('equivalent caller receipt releases ownership exactly once', () async {
      await fixture.repository.stopAcceptanceProcess(fixture.copyResult());
      final probes = fixture.probed.length;
      await expectLater(
        fixture.repository.stopAcceptanceProcess(fixture.launched),
        throwsStateError,
      );
      expect(fixture.stopped, hasLength(1));
      expect(
        fixture.probed,
        hasLength(probes),
        reason: 'Released ownership must be refused before native calls.',
      );
      expect(fixture.lookups, 0);
    });

    for (final failedCreation in [false, true]) {
      test(
        '${failedCreation ? 'failed' : 'retired'} request cannot claim a newer owned receipt',
        () async {
          final first = fixture.launched;
          await fixture.repository.stopAcceptanceProcess(first);
          var staleId = first.requestId;
          if (failedCreation) {
            fixture.failNextCreation = true;
            final failed = await fixture.launchAnother();
            expect(failed.processStarted, isFalse);
            expect(failed.requestId, isNotNull);
            staleId = failed.requestId;
          }
          final second = await fixture.launchAnother();
          expect(second.processStarted, isTrue, reason: second.message);
          expect(second.requestId, isNot(staleId));
          expect(
            second.process!.nativeStartToken,
            isNot(first.process!.nativeStartToken),
          );
          fixture.probed.clear();
          fixture.stopped.clear();
          final mixed = LaunchResult(
            started: true,
            message: 'Stale request paired with newer receipt',
            requestId: staleId,
            processId: second.processId,
            process: second.process,
          );
          await expectLater(
            Future.sync(
              () => fixture.repository.acceptanceAcknowledgement(mixed),
            ),
            throwsStateError,
          );
          await expectLater(
            fixture.repository.stopAcceptanceProcess(mixed),
            throwsStateError,
          );
          expect(fixture.probed, isEmpty);
          expect(fixture.stopped, isEmpty);
          expect(fixture.alive, isTrue);
          await fixture.repository.stopAcceptanceProcess(second);
          expect(fixture.stopped, [same(second.process)]);
          expect(fixture.lookups, 0);
        },
      );
    }

    test(
      'native cleanup remains available after repository monitor disposal',
      () async {
        await fixture.repository.dispose();
        await fixture.repository.stopAcceptanceProcess(fixture.copyResult());
        expect(fixture.stopped, [same(fixture.launched.process)]);
        expect(fixture.alive, isFalse);
        await expectLater(
          fixture.repository.stopAcceptanceProcess(fixture.launched),
          throwsStateError,
        );
      },
    );

    test(
      'disposal during process creation retains original cleanup ownership',
      () async {
        await fixture.repository.stopAcceptanceProcess(fixture.launched);
        fixture.stopped.clear();
        fixture.creationEntered = Completer<void>();
        fixture.creationRelease = Completer<void>();
        final pending = fixture.launchAnother();
        await fixture.creationEntered!.future.timeout(
          const Duration(seconds: 5),
        );
        await fixture.repository.dispose();
        fixture.creationRelease!.complete();
        final result = await pending;
        expect(result.processStarted, isTrue, reason: result.message);
        expect(result.latestActivity!.unconfirmed, isTrue);
        expect(
          await fixture.repository.acceptanceAcknowledgement(result),
          isNull,
        );
        await fixture.repository.stopAcceptanceProcess(result);
        expect(fixture.stopped, [same(result.process)]);
        expect(fixture.alive, isFalse);
        expect(fixture.lookups, 0);
      },
    );

    test(
      'unknown liveness retains request correlation for later owned cleanup',
      () async {
        fixture.livenessUnknown = true;
        await expectLater(
          fixture.repository.stopAcceptanceProcess(fixture.copyResult()),
          throwsStateError,
        );
        expect(fixture.stopped, isEmpty);
        expect(fixture.alive, isTrue);
        fixture.livenessUnknown = false;
        await fixture.repository.stopAcceptanceProcess(fixture.copyResult());
        expect(fixture.stopped, [same(fixture.launched.process)]);
        await expectLater(
          fixture.repository.stopAcceptanceProcess(fixture.launched),
          throwsStateError,
        );
      },
    );

    test(
      'changed generation, image, PID or request cannot claim owned cleanup',
      () async {
        final original = fixture.launched.process!;
        final changes = [
          fixture.copyResult(pid: original.pid + 1),
          fixture.copyResult(token: '${original.nativeStartToken}-stale'),
          fixture.copyResult(
            start: original.startTimeUtc.subtract(const Duration(seconds: 1)),
          ),
          fixture.copyResult(image: '${original.executablePath}.other'),
          fixture.copyResult(requestId: 'unowned-request'),
        ];
        for (final changed in changes) {
          await expectLater(
            fixture.repository.stopAcceptanceProcess(changed),
            throwsStateError,
          );
        }
        expect(fixture.stopped, isEmpty);
        expect(fixture.probed, isEmpty);
        expect(fixture.alive, isTrue);
        expect(fixture.lookups, 0);
        await fixture.repository.stopAcceptanceProcess(fixture.launched);
        expect(fixture.stopped, [same(original)]);
      },
    );
  }, skip: !Platform.isWindows);
}

final class _OwnershipFixture {
  _OwnershipFixture(this.isolation);
  final IsolationFixture isolation;
  late LocalLauncherRepository repository;
  late LaunchResult launched;
  final stopped = <LaunchProcessIdentity>[];
  final probed = <LaunchProcessIdentity>[];
  bool alive = false;
  bool failNextCreation = false;
  bool livenessUnknown = false;
  int lookups = 0;
  int starts = 0;
  Completer<void>? creationEntered;
  Completer<void>? creationRelease;

  static Future<_OwnershipFixture> create() async {
    final f = _OwnershipFixture(IsolationFixture());
    final game = f.isolation.game;
    void file(String relative) {
      final path = File(p.join(game.path, relative));
      path.parent.createSync(recursive: true);
      path.writeAsStringSync('');
    }

    // A synthetic installation: no runnable game or managed code is created.
    for (final path in [
      'Robotopia.exe',
      'Robotopia_Data/Managed/UnityEngine.dll',
      'winhttp.dll',
      'BepInEx/core/BepInEx.dll',
      for (final dll in topiaForgeRuntimeLoaderDlls)
        'BepInEx/plugins/TopiaForge.ModManager/$dll',
    ]) {
      file(path);
    }
    File(
      p.join(
        Directory.current.path,
        '..',
        '..',
        'third_party',
        'BepInEx',
        'win_x64_5.4.23.5',
        'doorstop_config.ini',
      ),
    ).copySync(p.join(game.path, 'doorstop_config.ini'));
    final root = Directory(p.join(f.isolation.root.path, 'repository'))
      ..createSync();
    f.repository = LocalLauncherRepository(
      dataRoot: f.isolation.launcher,
      repositoryRoot: root.path,
      knownGamePath: game.path,
      acceptanceIsolation: f.isolation.admit(),
      acceptanceChallenge: 'a' * 64,
      gameRunningProbe: (_) async => f.alive,
      gameProcessCreator: (request) async {
        if (f.failNextCreation) {
          f.failNextCreation = false;
          throw StateError(
            'Synthetic creation failure before any process exists.',
          );
        }
        f.creationEntered?.complete();
        await f.creationRelease?.future;
        f.alive = true;
        f.starts++;
        final identity = LaunchProcessIdentity(
          pid: 4241 + f.starts,
          executablePath: request.executable,
          startTimeUtc: DateTime.utc(2026, 9, 8, 0, 0, f.starts),
          nativeStartToken: 'windows:${1233 + f.starts}',
        );
        final requestId = request
            .environment[AcceptanceIsolationContext.environmentVariable]!;
        // Complete the ordinary monitor during its initial read. This keeps
        // callback assertions about stopAcceptanceProcess deterministic.
        File(
          p.join(
            f.isolation.game.path,
            'BepInEx',
            'TopiaForge',
            'staging',
            'launch-outcome-${LaunchStorageKeys.request(requestId)}.json',
          ),
        ).writeAsStringSync(
          jsonEncode(
            LaunchOutcome(
              kind: 'launch',
              requestId: requestId,
              command: 'main-menu',
              sequence: 1,
              phase: 'idle',
              status: 'succeeded',
            ).toJson(),
          ),
        );
        return LaunchProcessReceipt(pid: identity.pid, identity: identity);
      },
      gameProcessIdentityReader: (_, _) async {
        f.lookups++;
        throw StateError('A later PID lookup cannot establish ownership.');
      },
      gameProcessLiveness: (identity) async {
        f.probed.add(identity);
        return f.livenessUnknown ? null : f.alive;
      },
      gameProcessStopper: (identity) async {
        f.stopped.add(identity);
        f.alive = false;
        return true;
      },
    );
    try {
      final install = await f.repository.selectGameDirectory(game.path);
      f.launched = await f.repository.launch(
        install,
        const LauncherProfile(id: 'qa', name: 'QA'),
      );
      expect(f.launched.processStarted, isTrue, reason: f.launched.message);
      expect(f.launched.process, isNotNull);
      expect(f.launched.latestActivity!.acknowledged, isTrue);
      f.probed.clear();
      return f;
    } on Object {
      await f.dispose();
      rethrow;
    }
  }

  Future<LaunchResult> launchAnother() async {
    final install = await repository.selectGameDirectory(isolation.game.path);
    return repository.launch(
      install,
      const LauncherProfile(id: 'qa', name: 'QA'),
    );
  }

  LaunchResult copyResult({
    int? pid,
    String? token,
    DateTime? start,
    String? image,
    String? requestId,
  }) {
    final original = launched.process!;
    return LaunchResult(
      started: true,
      message: 'Equivalent transported receipt',
      requestId: requestId ?? launched.requestId,
      processId: pid ?? original.pid,
      process: LaunchProcessIdentity(
        pid: pid ?? original.pid,
        nativeStartToken: token ?? original.nativeStartToken,
        startTimeUtc: start ?? original.startTimeUtc,
        executablePath: image ?? original.executablePath,
      ),
    );
  }

  Future<void> dispose() async {
    await repository.dispose();
    isolation.dispose();
  }
}
