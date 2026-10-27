part of '../local_launcher_repository.dart';

extension _PublishedMapSourceProjection on LocalLauncherRepository {
  PublishedMapSourceStore get _publishedMapSources {
    if (_disposed) throw StateError('The launcher repository is closed.');
    _acceptanceIsolation?.verify();
    return PublishedMapSourceStore(_dataRoot.path);
  }

  // Called inside launch admission after the game is confirmed stopped. Runtime
  // discovery observes this immutable projection; no fabricated observation is used.
  Future<void> _stagePublishedMapSources(GameInstall install) async {
    try {
      final sources = await _publishedMapSources.read();
      final file = File(
        p.join(_managerRoot(install).path, PublishedMapSourceStore.fileName),
      );
      try {
        _requireRuntimeDirectory(
          Directory(install.path),
          file.parent,
          label: 'Saved map projection',
        );
      } on StateError {
        throw StateError(
          'Saved map projection requires ordinary folders without links. Repair the game folder and try again.',
        );
      }
      PublishedMapSourceStore.ordinaryPath(file.path);
      if (sources.isEmpty && !await file.exists()) return;
      if (await _runningForLaunch(install)) {
        throw StateError('Close Robotopia before updating its saved map list.');
      }
      await writeFileBytesAtomic(file, PublishedMapSources.write(sources));
    } on FileSystemException {
      throw StateError(
        'The saved map list could not be prepared for the game. Check folder access and try again.',
      );
    }
  }
}
