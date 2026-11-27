part of '../local_launcher_repository.dart';

extension _MapCacheCleanup on LocalLauncherRepository {
  Future<int> _clearDownloadedMaps(GameInstall install) async {
    final identity = _launchInstallIdentity(install);
    if (!_launchAdmissions.add(identity)) {
      throw StateError(
        'A launch is being prepared. Wait for it to finish before clearing maps.',
      );
    }
    LaunchStagingLease? lease;
    try {
      final game = Directory(install.path);
      final cache = Directory(
        p.join(_managerRoot(install).path, 'cache', 'maps'),
      );
      void ordinaryCache() {
        try {
          _requireRuntimeDirectory(game, cache, label: 'Downloaded map cache');
        } on StateError {
          throw StateError(
            'The downloaded map cache must use ordinary folders without links. No maps were cleared.',
          );
        }
      }

      ordinaryCache();
      lease = await LaunchStagingStore(install.path).acquireLaunchLease();
      if (lease == null) {
        throw StateError(
          'A launch or install is busy. Try clearing maps again when it finishes.',
        );
      }
      if (await _runningForLaunch(install)) {
        throw StateError(
          'Close Robotopia before clearing downloaded maps. The game must be confirmed closed.',
        );
      }
      if (!await cache.exists()) return 0;
      final files = <File>[];
      final names = RegExp(
        r'^(?:[a-f0-9]{64}\.map|(?:id-)?[A-Za-z0-9_-]{1,64}\.json|id-[A-Za-z0-9_-]{1,64}\.gone)(?:\.tmp-[a-f0-9]{32})?$',
      );
      await for (final entry in cache.list(followLinks: false)) {
        if (entry is! File || !names.hasMatch(p.basename(entry.path))) {
          throw StateError(
            'The downloaded map cache contains an unexpected entry. No maps were cleared.',
          );
        }
        files.add(entry);
      }
      ordinaryCache();
      if (await _runningForLaunch(install)) {
        throw StateError(
          'Robotopia started. Close it before clearing downloaded maps.',
        );
      }
      for (final file in files) {
        if (FileSystemEntity.typeSync(file.path, followLinks: false) !=
            FileSystemEntityType.file) {
          throw StateError(
            'The map cache changed while clearing. Try again once the game is closed.',
          );
        }
        await file.delete();
      }
      return files.length;
    } on FileSystemException {
      throw StateError(
        'Downloaded maps could not be cleared. Close the game and check that its folder can be written to.',
      );
    } finally {
      await lease?.release();
      _launchAdmissions.remove(identity);
    }
  }
}
