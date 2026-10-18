part of '../local_launcher_repository.dart';

/// What a join installs, decided before anything changes, and decided again
/// once a running game is closed, since a game writes its manager state.
extension _JoinInstallPreflight on _JoinInstall {
  /// Decides what to install, refusing before anything changes: a version
  /// installed as another archive is never replaced, an archive already
  /// installed as the server runs it needs nothing, and a record of server
  /// content that cannot be used, or that would pass its bound, refuses.
  Future<List<_PendingJoinPackage>> _decide() async {
    final Map<String, Object?> state;
    try {
      state = await _repository._readManagerState(_install);
    } on Object catch (error) {
      await _log('A join install could not read the manager state: $error');
      throw const _JoinInstallRefused(
        "The game's list of mods could not be read. Open Mods to repair it, "
        'then join again.',
      );
    }
    final recorded = _repository._stateByModId(state).keys.toSet();
    final load = await _repository._serverContentStore(_install).load();
    if (!load.status.isUsable) {
      throw _JoinInstallRefused(_serverContentUnusable(load.status, 'join'));
    }
    final named = {
      for (final profile in await _repository._loadProfiles())
        if (!ServerProfiles.isServerProfile(profile))
          for (final id in [
            ...profile.enabledMods,
            ...profile.selectedVersions.keys,
          ])
            id.toLowerCase(),
    };
    final packagesRoot = _repository._packagesRoot(_install);
    final folders = {
      for (final entity in packagesRoot.listSync(followLinks: false))
        if (entity is Directory)
          p.basename(entity.path).toLowerCase(): p.basename(entity.path),
    };
    final pending = <_PendingJoinPackage>[];
    for (final item in _plan.packages) {
      if (item.disposition != JoinPackageDisposition.download) continue;
      final manifest = item.manifest!;
      final key = manifest.id.toLowerCase();
      final folder = folders[key];
      final name = _joinModName(item);
      if (folder != null && folder != manifest.id) {
        throw _JoinInstallRefused(
          'You have ${manifest.name} installed under a folder named '
          'differently than the server version needs, so it cannot be added '
          'beside it. Uninstall ${manifest.name} in Mods, then join again.',
        );
      }
      final target = Directory(
        p.join(packagesRoot.path, manifest.id, manifest.version),
      );
      final type = FileSystemEntity.typeSync(target.path, followLinks: false);
      if (type != FileSystemEntityType.notFound) {
        if (type == FileSystemEntityType.directory &&
            await _receiptArchiveSha256(target) == item.package.sha256) {
          continue;
        }
        throw _JoinInstallRefused(
          'You have $name installed, but as a different build than the '
          'server runs, and joining never replaces your copy. Uninstall it in '
          'Mods, then join again.',
        );
      }
      pending.add(
        _PendingJoinPackage(
          item,
          hidden:
              recorded.contains(key) || folder != null || named.contains(key),
          addRecord: !recorded.contains(key) && folder == null,
        ),
      );
    }
    final kept = load.record.entries.where(
      (entry) => !pending.any(
        (item) => entry.names(item.manifest.id, item.manifest.version),
      ),
    );
    if (kept.length + pending.length > ServerContentRecord.maximumEntries) {
      throw const _JoinInstallRefused(
        'Too many mods are installed for servers. Clean up server content in '
        'Settings, then join again.',
      );
    }
    return pending;
  }

  /// Whether [decided] is what was staged: the same packages, each as
  /// hidden and as new to the player as before.
  bool _decidedAsStaged(List<_PendingJoinPackage> decided) {
    if (decided.length != _pending.length) return false;
    for (var index = 0; index < decided.length; index++) {
      final now = decided[index];
      final then = _pending[index];
      if (now.manifest.id != then.manifest.id ||
          now.manifest.version != then.manifest.version ||
          now.hidden != then.hidden ||
          now.addRecord != then.addRecord) {
        return false;
      }
    }
    return true;
  }
}
