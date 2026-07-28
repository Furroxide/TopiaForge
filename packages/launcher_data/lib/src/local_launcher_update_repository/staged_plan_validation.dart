part of '../local_launcher_update_repository.dart';

void _ensureLauncherUpdateStorage(
  Directory dataRoot,
  Directory updatesRoot,
  Directory transactionsRoot,
) {
  if (FileSystemEntity.typeSync(dataRoot.path, followLinks: false) ==
      FileSystemEntityType.notFound) {
    dataRoot.createSync(recursive: true);
  }
  for (final directory in [dataRoot, updatesRoot, transactionsRoot]) {
    final type = FileSystemEntity.typeSync(directory.path, followLinks: false);
    if (type == FileSystemEntityType.notFound) {
      directory.createSync();
    } else if (type != FileSystemEntityType.directory) {
      throw StateError('Launcher update storage is unsafe.');
    }
  }
}

void _deleteFailedStaging(Directory? directory) {
  if (directory == null) return;
  final type = FileSystemEntity.typeSync(directory.path, followLinks: false);
  if (type == FileSystemEntityType.directory) {
    directory.deleteSync(recursive: true);
  }
}

void _validateLauncherStagedPlan(
  File planFile,
  LauncherUpdateTransactionPlan plan,
  LauncherInstallationLayout layout,
  Directory transactionsRoot,
  int launcherPid,
) {
  final transactionRoot = p.join(transactionsRoot.path, plan.transactionId);
  final stagingContainer = p.join(
    p.dirname(layout.targetRoot),
    '.topiaforge-update-${plan.transactionId}.staged',
  );
  final expectedStagedRoot = layout.installLayout == 'app-bundle'
      ? p.join(stagingContainer, 'TopiaForge.app')
      : stagingContainer;
  final expected = {
    planFile.path: p.join(transactionRoot, 'plan.json'),
    plan.targetRoot: layout.targetRoot,
    plan.stagedRoot: expectedStagedRoot,
    plan.backupRoot: p.join(
      p.dirname(layout.targetRoot),
      '.topiaforge-backup-${plan.transactionId}',
    ),
    plan.failedRoot: p.join(
      p.dirname(layout.targetRoot),
      '.topiaforge-failed-${plan.transactionId}',
    ),
    plan.healthFile: p.join(transactionRoot, 'health.json'),
    plan.journalFile: p.join(transactionRoot, 'journal.json'),
  };
  if (plan.platformId != layout.platformId ||
      plan.launcherPid != launcherPid ||
      plan.launcherRelativePath != layout.launcherRelativePath ||
      expected.entries.any(
        (entry) => !p.equals(
          p.normalize(p.absolute(entry.key)),
          p.normalize(p.absolute(entry.value)),
        ),
      )) {
    throw StateError(
      'The staged update plan does not match this installation.',
    );
  }
}
