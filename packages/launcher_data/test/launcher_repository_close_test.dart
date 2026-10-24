import 'dart:async';
import 'dart:convert';
import 'dart:io';

import 'package:archive/archive.dart';
import 'package:launcher_data/launcher_data.dart';
import 'package:launcher_domain/launcher_domain.dart';
import 'package:path/path.dart' as p;
import 'package:test/test.dart';

import 'open_handle_support.dart';

// On slow Windows runners a test has run past its 30 s timeout with
// repository work still going: a launch preparing, or a package install. The
// tear-down then closed the repository, which returned at once, so deleting
// the test's folder failed while that work still held a file in it ("being
// used by another process"), and the work later failed the finished test,
// with "The launcher repository is closed." or by reading the next test's
// folder. Each test below holds that work still where it was, closes the
// repository, and lets the work go on.
void main() {
  late Directory root;
  late Directory gameRoot;
  late Directory dataRoot;
  late Directory repositoryRoot;

  setUp(() {
    root = Directory(
      Directory.systemTemp
          .createTempSync('repository-close-')
          .resolveSymbolicLinksSync(),
    );
    addTearDown(() => root.deleteSync(recursive: true));
    gameRoot = Directory(p.join(root.path, 'TopiaForge'))..createSync();
    dataRoot = Directory(p.join(root.path, 'data'));
    repositoryRoot = Directory(p.join(root.path, 'repo'))..createSync();
    _createGame(gameRoot);
    _createRuntimeSources(repositoryRoot);
  });

  test(
    'closing waits for a launch still preparing, which stops and leaves nothing open',
    () async {
      final preparing = Completer<void>();
      final proceed = Completer<void>();
      var hold = false;
      final repository = LocalLauncherRepository(
        dataRoot: dataRoot.path,
        repositoryRoot: repositoryRoot.path,
        gameProcessStarter: (_) async => 42,
        gameRunningProbe: (_) async {
          if (hold) {
            preparing.complete();
            await proceed.future;
          }
          return false;
        },
      );
      final work = RepositoryWork()..checkAtEnd(root);
      addTearDown(repository.dispose);
      addTearDown(() {
        if (!proceed.isCompleted) proceed.complete();
      });
      var install = await repository.selectGameDirectory(gameRoot.path);
      expect((await repository.installOrRepairRuntime(install)).ok, isTrue);
      install = await repository.selectGameDirectory(gameRoot.path);

      hold = true;
      final launching = work.track(
        repository.launch(
          install,
          const LauncherProfile(id: 'menu', name: 'Menu'),
        ),
      );
      await preparing.future;
      final open = openHandlesUnder(root);
      if (open != null) {
        expect(
          open,
          contains(
            p.join(
              'TopiaForge',
              'BepInEx',
              'TopiaForge',
              'staging',
              'launch-admission.lock',
            ),
          ),
          reason: 'A launch holds the staging lease while it prepares.',
        );
      }

      var closed = false;
      final closing = repository.dispose().then((_) => closed = true);
      await pumpEventQueue();
      expect(closed, isFalse, reason: 'The launch has not stopped yet.');
      proceed.complete();
      await closing;
      expect(work.finished, isTrue, reason: 'The launch stopped first.');
      expectNothingOpenUnder(root);
      final result = await launching;
      expect(result.started, isFalse);
      expect(result.message, 'The launcher repository is closed.');
    },
  );

  test(
    'closing waits for a package install still committing, which finishes and leaves nothing open',
    () async {
      final committing = Completer<void>();
      final proceed = Completer<void>();
      final repository = LocalLauncherRepository(
        dataRoot: dataRoot.path,
        repositoryRoot: repositoryRoot.path,
        packageMetadataValidator: (_) async => const [],
        packageInstallCommitHook: (_) async {
          committing.complete();
          await proceed.future;
        },
      );
      final work = RepositoryWork()..checkAtEnd(root);
      addTearDown(repository.dispose);
      addTearDown(() {
        if (!proceed.isCompleted) proceed.complete();
      });
      final install = await repository.selectGameDirectory(gameRoot.path);

      final installing = work.track(
        repository.installPackage(_package(root, 'held.mod').path, install),
      );
      await committing.future;

      var closed = false;
      final closing = repository.dispose().then((_) => closed = true);
      await pumpEventQueue();
      expect(closed, isFalse, reason: 'The install has not finished yet.');
      proceed.complete();
      await closing;
      expect(work.finished, isTrue, reason: 'The install finished first.');
      expectNothingOpenUnder(root);
      final installed = await installing;
      expect(installed.map((mod) => mod.id), contains('held.mod'));
    },
  );

  test(
    'repeated close waits for failed work without replacing its error',
    () async {
      final committing = Completer<void>();
      final proceed = Completer<void>();
      final failure = StateError('Synthetic install failure.');
      final repository = LocalLauncherRepository(
        dataRoot: dataRoot.path,
        repositoryRoot: repositoryRoot.path,
        packageMetadataValidator: (_) async => const [],
        packageInstallCommitHook: (_) async {
          committing.complete();
          await proceed.future;
          throw failure;
        },
      );
      final work = RepositoryWork()..checkAtEnd(root);
      addTearDown(repository.dispose);
      addTearDown(() {
        if (!proceed.isCompleted) proceed.complete();
      });
      final install = await repository.selectGameDirectory(gameRoot.path);
      final installing = work.track(
        repository.installPackage(_package(root, 'failed.mod').path, install),
      );
      final observed = expectLater(installing, throwsA(same(failure)));
      await committing.future;
      final closing = repository.dispose();
      expect(repository.dispose(), same(closing));
      var closed = false;
      final completion = closing.then((_) => closed = true);
      await pumpEventQueue();
      expect(closed, isFalse);
      proceed.complete();
      await completion;
      await observed;
      expect(work.finished, isTrue);
      expectNothingOpenUnder(root);
    },
  );

  test(
    'closing also waits for work started while it waits, which finishes and leaves nothing open',
    () async {
      final committing = Completer<void>();
      final proceed = Completer<void>();
      final repairing = Completer<void>();
      final repairProceeds = Completer<void>();
      final repository = LocalLauncherRepository(
        dataRoot: dataRoot.path,
        repositoryRoot: repositoryRoot.path,
        packageMetadataValidator: (_) async => const [],
        packageInstallCommitHook: (_) async {
          committing.complete();
          await proceed.future;
        },
        runtimeRepairCommitHook: (_) async {
          if (repairing.isCompleted) return;
          repairing.complete();
          await repairProceeds.future;
        },
      );
      final work = RepositoryWork()..checkAtEnd(root);
      addTearDown(repository.dispose);
      addTearDown(() {
        for (final gate in [proceed, repairProceeds]) {
          if (!gate.isCompleted) gate.complete();
        }
      });
      final install = await repository.selectGameDirectory(gameRoot.path);
      final installing = work.track(
        repository.installPackage(_package(root, 'held.mod').path, install),
      );
      await committing.future;

      var closed = false;
      final closing = repository.dispose().then((_) => closed = true);
      // A caller's next step can start more work while the close waits, as
      // a refresh after an install does, and the close waits for it too.
      final repairs = work.track(repository.installOrRepairRuntime(install));
      proceed.complete();
      await installing;
      await repairing.future;
      await pumpEventQueue();
      expect(
        closed,
        isFalse,
        reason: 'The repair started during the close has not finished yet.',
      );
      repairProceeds.complete();
      await closing;
      expect(work.finished, isTrue, reason: 'The repair finished first.');
      expectNothingOpenUnder(root);
      expect((await repairs).ok, isTrue);
    },
  );
}

void _createGame(Directory gameRoot) {
  File(p.join(gameRoot.path, 'Robotopia.exe')).writeAsStringSync('');
  Directory(
    p.join(gameRoot.path, 'Robotopia_Data', 'Managed'),
  ).createSync(recursive: true);
  File(
    p.join(gameRoot.path, 'Robotopia_Data', 'Managed', 'UnityEngine.dll'),
  ).writeAsStringSync('');
}

void _createRuntimeSources(Directory repositoryRoot) {
  final bepInEx = Directory(
    p.join(repositoryRoot.path, 'third_party', 'BepInEx', 'win_x64_5.4.23.5'),
  )..createSync(recursive: true);
  File(p.join(bepInEx.path, 'winhttp.dll')).writeAsStringSync('');
  File(p.join(bepInEx.path, 'doorstop_config.ini')).writeAsStringSync('');
  Directory(
    p.join(bepInEx.path, 'BepInEx', 'core'),
  ).createSync(recursive: true);
  File(
    p.join(bepInEx.path, 'BepInEx', 'core', 'BepInEx.dll'),
  ).writeAsStringSync('');
  final loader = Directory(
    p.join(
      repositoryRoot.path,
      'src',
      'TopiaForge.ModManager',
      'bin',
      'Release',
      'netstandard2.1',
    ),
  )..createSync(recursive: true);
  for (final name in topiaForgeRuntimeLoaderDlls) {
    File(p.join(loader.path, name)).writeAsStringSync('');
  }
}

File _package(Directory root, String id) {
  final file = File(p.join(root.path, '$id-1.0.0.topiaforgemod'));
  final archive = Archive()
    ..addFile(
      ArchiveFile.string(
        'topiaforge.mod.json',
        jsonEncode({
          'schemaVersion': 6,
          'name': id,
          'displayName': id,
          'version': '1.0.0',
          'author': {'name': 'TopiaForge'},
          'entryAssembly': 'HeldMod.dll',
          'entryType': '$id.Entry',
          'supportedGameVersionRange': '*',
          'supportedLoaderVersionRange': '*',
          'supportedSdkVersionRange': '*',
        }),
      ),
    )
    ..addFile(ArchiveFile.string('HeldMod.dll', '1.0.0'));
  file.writeAsBytesSync(ZipEncoder().encode(archive));
  return file;
}
