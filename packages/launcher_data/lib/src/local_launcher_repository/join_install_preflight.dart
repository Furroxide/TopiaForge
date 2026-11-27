part of '../local_launcher_repository.dart';

/// What a join installs, decided before anything changes, and decided again
/// once a running game is closed, since a game writes its manager state.
extension _JoinInstallPreflight on _JoinInstall {
  /// Decides what to install, refusing before anything changes: a version
  /// installed as another archive is never replaced, an archive already
  /// installed as the server runs it needs nothing, a version the plan found
  /// installed must still be there, and a record of server content that
  /// cannot be used, or that would pass its bound, refuses.
  Future<List<_PendingJoinPackage>> _decide() async {
    final Map<String, Object?> state;
    try {
      state = await _repository._readManagerState(_install);
    } on Object catch (error) {
      await _log('A join install could not read the manager state: $error');
      throw const _JoinInstallRefused(
        "The game's list of mods, BepInEx/TopiaForge/state.json, could not be "
        'read. Close any program using the game folder, then join again. If '
        'it still cannot be read, it is damaged: repair or restore that file '
        'first.',
      );
    }
    final recorded = _repository._stateByModId(state).keys.toSet();
    final load = await _repository._serverContentStore(_install).load();
    if (!load.status.isUsable) {
      throw _JoinInstallRefused(_serverContentUnusable(load.status, 'join'));
    }
    // An earlier join whose rollback could not finish keeps its folders, and
    // their record entries, until a later sweep takes them out, so nothing
    // may count them as installed meanwhile. Nor may anything build on one
    // whose journal cannot be read just now: what it left is unknown, and
    // the refusal says what to do without naming the file. This join's own
    // folder, there once it is staged, is no earlier join's.
    final bool unsettled;
    try {
      unsettled = await _repository._hasUnsettledJoinInstall(
        _install,
        except: _root,
      );
    } on LauncherFileUnreadable catch (unreadable) {
      throw _JoinInstallRefused(unreadable.message);
    }
    if (unsettled) {
      throw const _JoinInstallRefused(
        'A join that failed earlier is still being put back: some of its '
        'files are in use or could not be removed yet. Close any program '
        'using the game folder, such as an antivirus scan, and make sure the '
        'drive has free space, then join again.',
      );
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
      if (item.disposition == JoinPackageDisposition.useInstalled) {
        // The plan was made before this install's lease and sweep, so what
        // it found installed must still be there, as the same archive.
        final installed = item.manifest!;
        final folder = folders[installed.id.toLowerCase()];
        final target = Directory(
          p.join(packagesRoot.path, folder ?? installed.id, installed.version),
        );
        if (folder == null ||
            FileSystemEntity.typeSync(target.path, followLinks: false) !=
                FileSystemEntityType.directory ||
            await _receiptArchiveSha256(target) != item.package.sha256) {
          throw const _JoinInstallRefused(_joinModsChanged);
        }
        continue;
      }
      if (item.disposition != JoinPackageDisposition.download &&
          item.disposition != JoinPackageDisposition.downloadFromHost) {
        continue;
      }
      final manifest = item.manifest ?? await _hostArchiveManifest(item);
      final key = manifest.id.toLowerCase();
      final folder = folders[key];
      final name = _joinModName(item);
      if (folder != null && folder != manifest.id) {
        // A host archive's display name is not validated until staging, so
        // this earlier refusal names the mod as the plan does. The folder
        // under another casing of its id can hold any version: the
        // server's is left out.
        final mod = _joinModTitle(item);
        throw _JoinInstallRefused(
          'You have $mod installed under a folder named '
          'differently than the server version needs, so it cannot be added '
          'beside it. Uninstall $mod in Mods, then join again.',
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
        final other = load.record.entryFor(manifest.id, manifest.version);
        throw _JoinInstallRefused(
          other != null &&
                  other.hidden &&
                  other.receiptSha256 == await _receiptSha256(target)
              ? 'Another server you joined runs a different build of $name, '
                    'and joining never replaces it. Remove that server\'s '
                    'content, then join again.'
              : 'You have $name installed, but as a different build than '
                    'the server runs, and joining never replaces your copy. '
                    'Uninstall it in Mods, then join again.',
        );
      }
      pending.add(
        _PendingJoinPackage(
          item,
          manifest: manifest,
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
        'Too many mods are installed for servers. Remove the content of '
        'servers you no longer join, then join again.',
      );
    }
    return pending;
  }

  /// The manifest of [item]'s archive, which only the host served, from the
  /// package cache, where the download verified it; read once per install.
  /// Refuses when it is not there, or does not name the mod and version the
  /// descriptor claims.
  Future<ModManifest> _hostArchiveManifest(JoinPlanPackage item) async {
    final sha256 = item.package.sha256;
    final known = _hostManifests[sha256];
    if (known != null) return known;
    final name = _joinModName(item);
    final ModManifest manifest;
    try {
      manifest = (await _repository._readPackage(
        _repository._joinCacheFile(sha256).path,
        expectedSha256: sha256,
      )).manifest;
    } on Object {
      throw _JoinInstallRefused(
        'The download of $name is missing or damaged. Join again to download '
        'it.',
      );
    }
    if (!_namesHostPackage(manifest, item.package)) {
      throw _JoinInstallRefused(
        'The download of $name holds another mod or version than the server '
        'lists. Join again to download it afresh.',
      );
    }
    return _hostManifests[sha256] = manifest;
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

/// Why an install ends when what its plan or its staging was made from
/// changed before it began; the join's next plan sees what is there now.
const _joinModsChanged =
    'Your mods in this game changed while the join was preparing. Join again.';
