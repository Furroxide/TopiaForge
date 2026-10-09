part of '../local_launcher_repository.dart';

/// Installs and checks the licence texts that accompany the loader.
///
/// They are staged in the same transaction as the loader assemblies, so
/// commit, rollback, and interrupted-repair recovery treat both alike.
extension _RuntimeLoaderNotices on LocalLauncherRepository {
  File _runtimeLoaderNoticeSource(TopiaForgeRuntimeLoaderNotice notice) => File(
    p.joinAll([_repositoryRoot.path, ...p.posix.split(notice.sourcePath)]),
  );

  /// Canonical sources this payload lacks, as repository-relative paths.
  List<String> _missingRuntimeLoaderNoticeSources() => {
    for (final notice in topiaForgeRuntimeLoaderNotices)
      if (FileSystemEntity.typeSync(
            _runtimeLoaderNoticeSource(notice).path,
            followLinks: false,
          ) !=
          FileSystemEntityType.file)
        notice.sourcePath,
  }.toList();

  Future<void> _stageRuntimeLoaderNotices(
    _RuntimeRepairTransaction transaction,
  ) async {
    for (final notice in topiaForgeRuntimeLoaderNotices) {
      final source = _runtimeLoaderNoticeSource(notice);
      _requireRuntimeDirectory(
        _repositoryRoot,
        source.parent,
        label: 'Loader notice source',
      );
      await transaction.addSource(
        source,
        p.posix.join(
          'BepInEx',
          'plugins',
          'TopiaForge.ModManager',
          topiaForgeRuntimeLoaderNoticeDirectory,
          notice.fileName,
        ),
      );
    }
  }

  /// Whether [pluginDirectory] carries this payload's notices unchanged.
  ///
  /// A loader installed before its notices travelled with it reads as stale,
  /// so the repair that runs before launch adds them. A payload without a
  /// complete notice set has nothing to install, so detection then rests on
  /// the assemblies alone rather than demanding a repair that cannot succeed.
  Future<bool> _runtimeLoaderNoticesCurrent(Directory pluginDirectory) async {
    if (_missingRuntimeLoaderNoticeSources().isNotEmpty) {
      return true;
    }
    for (final notice in topiaForgeRuntimeLoaderNotices) {
      final installed = File(
        p.join(
          pluginDirectory.path,
          topiaForgeRuntimeLoaderNoticeDirectory,
          notice.fileName,
        ),
      );
      if (FileSystemEntity.typeSync(installed.path, followLinks: false) !=
              FileSystemEntityType.file ||
          !await _sameFileContents(
            installed,
            _runtimeLoaderNoticeSource(notice),
          )) {
        return false;
      }
    }
    return true;
  }
}
