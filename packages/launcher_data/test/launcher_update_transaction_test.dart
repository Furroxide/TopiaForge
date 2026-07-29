import 'dart:convert';
import 'dart:io';

import 'package:launcher_data/launcher_data.dart';
import 'package:path/path.dart' as p;
import 'package:test/test.dart';

void main() {
  late Directory root;

  setUp(() async {
    root = await Directory.systemTemp.createTemp(
      'topiaforge-update-transaction-',
    );
  });

  tearDown(() async {
    if (root.existsSync()) await root.delete(recursive: true);
  });

  test('atomically swaps and commits only after the health nonce', () async {
    final plan = _plan(root);
    _seedInstall(plan.targetRoot, 'old');
    _seedInstall(plan.stagedRoot, 'new');
    final planFile = _writePlan(plan);
    final helper = LauncherUpdateTransactionHelper(
      processExists: (_) async => false,
      processKiller: (_) => true,
      pollInterval: const Duration(milliseconds: 1),
      processLauncher: (current, {required healthHandshake}) async {
        if (healthHandshake) {
          File(current.healthFile).writeAsStringSync(
            jsonEncode({
              'formatVersion': 1,
              'nonce': current.healthNonce,
              'healthy': true,
            }),
          );
        }
        return 999999;
      },
    );

    await helper.apply(planFile.path);

    expect(
      File(p.join(plan.targetRoot, 'version.txt')).readAsStringSync(),
      'new',
    );
    expect(Directory(plan.backupRoot).existsSync(), isFalse);
    expect(_journal(plan)['phase'], 'complete');
  });

  test('failure after relaunch rolls the entire package back', () async {
    final plan = _plan(root);
    _seedInstall(plan.targetRoot, 'old');
    _seedInstall(plan.stagedRoot, 'new');
    final planFile = _writePlan(plan);
    final helper = LauncherUpdateTransactionHelper(
      processExists: (_) async => false,
      processKiller: (_) => true,
      pollInterval: const Duration(milliseconds: 1),
      processLauncher: (_, {required healthHandshake}) async => 999998,
      transitionHook: (phase, _) {
        if (phase == 'relaunched') {
          throw StateError('injected post-relaunch failure');
        }
      },
    );

    await expectLater(helper.apply(planFile.path), throwsStateError);

    expect(
      File(p.join(plan.targetRoot, 'version.txt')).readAsStringSync(),
      'old',
    );
    expect(
      File(p.join(plan.failedRoot, 'version.txt')).readAsStringSync(),
      'new',
    );
    expect(_journal(plan)['phase'], 'rolled-back');
  });

  test('an interrupted backup transition is recovered idempotently', () async {
    final plan = _plan(root);
    _seedInstall(plan.targetRoot, 'old');
    _seedInstall(plan.stagedRoot, 'new');
    final planFile = _writePlan(plan);
    final interrupted = LauncherUpdateTransactionHelper(
      processExists: (_) async => false,
      processKiller: (_) => true,
      pollInterval: const Duration(milliseconds: 1),
      processLauncher: (_, {required healthHandshake}) async => 999997,
      transitionHook: (phase, _) {
        if (phase == 'backup-created') {
          throw const LauncherUpdateInterruption('simulated power loss');
        }
      },
    );

    await expectLater(
      interrupted.apply(planFile.path),
      throwsA(isA<LauncherUpdateInterruption>()),
    );
    expect(Directory(plan.targetRoot).existsSync(), isFalse);
    expect(Directory(plan.backupRoot).existsSync(), isTrue);

    final recovery = LauncherUpdateTransactionHelper(
      processExists: (_) async => false,
      processKiller: (_) => true,
      pollInterval: const Duration(milliseconds: 1),
      processLauncher: (_, {required healthHandshake}) async => 999996,
    );
    await recovery.recover(planFile.path);
    await recovery.recover(planFile.path);

    expect(
      File(p.join(plan.targetRoot, 'version.txt')).readAsStringSync(),
      'old',
    );
    expect(_journal(plan)['phase'], 'rolled-back');
  });

  for (final platform in const [
    'windows-x64',
    'linux-x64',
    'macos-universal',
  ]) {
    for (final moment in const ['before', 'after']) {
      for (final phase in const [
        'waiting',
        'backing-up',
        'backup-created',
        'installing',
        'installed',
        'launching',
        'relaunched',
        'healthy',
        'committing',
        'complete',
      ]) {
        test('$platform recovers interruption $moment $phase', () async {
          final caseRoot = Directory(
            p.join(root.path, '$platform-$moment-$phase'),
          )..createSync(recursive: true);
          final plan = _plan(caseRoot, platformId: platform);
          _seedInstall(
            plan.targetRoot,
            'old',
            launcherRelativePath: plan.launcherRelativePath,
          );
          _seedInstall(
            plan.stagedRoot,
            'new',
            launcherRelativePath: plan.launcherRelativePath,
          );
          if (platform == 'macos-universal') {
            File(
              p.join(p.dirname(plan.stagedRoot), 'topiaforge'),
            ).writeAsStringSync('root shim');
          }
          final planFile = _writePlan(plan);
          final phaseNeedsHealth = const {
            'healthy',
            'committing',
            'complete',
          }.contains(phase);
          final helper = LauncherUpdateTransactionHelper(
            processExists: (_) async => false,
            processKiller: (_) => true,
            pollInterval: const Duration(milliseconds: 1),
            processLauncher: (current, {required healthHandshake}) async {
              if (healthHandshake && phaseNeedsHealth) {
                _writeHealth(current);
              }
              return 900001;
            },
            beforeTransitionHook: moment == 'before'
                ? (currentPhase, _) {
                    if (currentPhase == phase) {
                      throw LauncherUpdateInterruption(
                        'interrupted before $phase',
                      );
                    }
                  }
                : null,
            transitionHook: moment == 'after'
                ? (currentPhase, _) {
                    if (currentPhase == phase) {
                      throw LauncherUpdateInterruption(
                        'interrupted after $phase',
                      );
                    }
                  }
                : null,
          );

          await expectLater(
            helper.apply(planFile.path),
            throwsA(isA<LauncherUpdateInterruption>()),
          );

          var rollbackLaunches = 0;
          final recovery = LauncherUpdateTransactionHelper(
            processExists: (_) async => false,
            processKiller: (_) => true,
            pollInterval: const Duration(milliseconds: 1),
            processLauncher: (_, {required healthHandshake}) async {
              if (!healthHandshake) rollbackLaunches += 1;
              return 900002;
            },
          );
          await recovery.recover(planFile.path);
          await recovery.recover(planFile.path);

          final committed = phaseNeedsHealth;
          expect(
            File(p.join(plan.targetRoot, 'version.txt')).readAsStringSync(),
            committed ? 'new' : 'old',
          );
          expect(
            _journal(plan)['phase'],
            committed ? 'complete' : 'rolled-back',
          );
          expect(rollbackLaunches, 0);
          expect(Directory(plan.backupRoot).existsSync(), isFalse);
          if (platform == 'macos-universal') {
            expect(Directory(p.dirname(plan.stagedRoot)).existsSync(), isFalse);
          }
        });
      }
    }
  }
}

LauncherUpdateTransactionPlan _plan(
  Directory root, {
  String platformId = 'windows-x64',
}) {
  const transactionId = '0123456789abcdef0123456789abcdef';
  final transactionRoot = Directory(
    p.join(root.path, 'transactions', transactionId),
  )..createSync(recursive: true);
  final isMac = platformId == 'macos-universal';
  final launcherRelativePath = isMac
      ? p.join('Contents', 'MacOS', 'topiaforge_launcher')
      : platformId == 'windows-x64'
      ? 'launcher.exe'
      : 'launcher';
  final stagingContainer = p.join(
    root.path,
    '.topiaforge-update-$transactionId.staged',
  );
  return LauncherUpdateTransactionPlan(
    transactionId: transactionId,
    platformId: platformId,
    targetRoot: p.join(root.path, isMac ? 'TopiaForge.app' : 'TopiaForge'),
    stagedRoot: isMac
        ? p.join(stagingContainer, 'TopiaForge.app')
        : stagingContainer,
    backupRoot: p.join(root.path, '.topiaforge-backup-$transactionId'),
    failedRoot: p.join(root.path, '.topiaforge-failed-$transactionId'),
    launcherRelativePath: launcherRelativePath,
    launcherPid: 12345,
    healthNonce: List.filled(64, 'a').join(),
    healthFile: p.join(transactionRoot.path, 'health.json'),
    journalFile: p.join(transactionRoot.path, 'journal.json'),
    healthTimeoutSeconds: 5,
  );
}

File _writePlan(LauncherUpdateTransactionPlan plan) {
  final file = File(p.join(p.dirname(plan.journalFile), 'plan.json'));
  plan.write(file);
  return file;
}

void _seedInstall(
  String path,
  String version, {
  String launcherRelativePath = 'launcher.exe',
}) {
  final root = Directory(path)..createSync(recursive: true);
  File(p.join(root.path, launcherRelativePath))
    ..parent.createSync(recursive: true)
    ..writeAsStringSync('fixture');
  File(p.join(root.path, 'version.txt')).writeAsStringSync(version);
}

void _writeHealth(LauncherUpdateTransactionPlan plan) {
  File(plan.healthFile).writeAsStringSync(
    jsonEncode({
      'formatVersion': 1,
      'nonce': plan.healthNonce,
      'healthy': true,
      'processId': 900001,
    }),
  );
}

Map<String, Object?> _journal(LauncherUpdateTransactionPlan plan) =>
    Map<String, Object?>.from(
      jsonDecode(File(plan.journalFile).readAsStringSync()) as Map,
    );
