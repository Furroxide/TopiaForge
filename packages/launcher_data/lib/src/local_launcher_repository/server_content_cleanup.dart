part of '../local_launcher_repository.dart';

/// A cleanup's plan, and what applying it needs: the record entry of each
/// removable version, the record as the plan read it, and what the record
/// still names that is no longer there: entries and withheld versions whose
/// folder is gone or was installed again since, and added records of mods
/// with neither a manager record nor a version left.
typedef _ServerContentWork = ({
  ServerContentCleanupPlan plan,
  List<ServerContentEntry> removable,
  ServerContentRecord record,
  List<ServerContentEntry> stale,
  List<WithheldServerVersion> staleWithheld,
  Set<String> staleAdded,
});

/// The data half of Settings → Multiplayer → server content: a plan of what
/// joins installed into an install, version by version, and its exact
/// application.
///
/// - **What is server content.** A version a join installed whose install
///   receipt is still the one the join wrote. The player's own installs, and
///   versions installed again since, never appear.
/// - **What may go.** A version no profile of the player's own loads or pins,
///   and the manager state does not have on (what a game started without
///   the launcher loads), whose pinning server profiles all go too. A
///   pending uninstall counts as loading nothing. A version no server
///   profile pins goes in every cleanup, and after each join on its own
///   ([_removeUnpinnedServerContent]), so the player only ever decides about
///   whole servers.
/// - **What is never judged.** A version whose install receipt cannot be
///   read just now is neither listed nor dropped from the record, so a file
///   another program holds never turns a server's version into the
///   player's own.
/// - **Applying** follows the uninstall rules: the version folders go, and a
///   mod left with none loses its manager record when a join added it or its
///   uninstall is pending, as an uninstall removes it. It refuses while the
///   game runs, while a launch or install holds the staging lease, and once
///   anything the plan was made from changed.
extension _ServerContentCleanup on LocalLauncherRepository {
  Future<ServerContentCleanupPlan> _planServerContentCleanup(
    GameInstall install, {
    Set<String>? scope,
  }) async => (await _serverContentWork(install, scope: scope)).plan;

  Future<_ServerContentWork> _serverContentWork(
    GameInstall install, {
    Set<String>? scope,
  }) async {
    final load = await _serverContentStore(install).load();
    if (!load.status.isUsable) {
      throw StateError(_serverContentUnusable(load.status, 'cleanup'));
    }
    final Map<String, Map<dynamic, dynamic>> state;
    try {
      state = _stateByModId(await _readManagerState(install));
    } on Object {
      throw StateError(
        "The game's list of mods, BepInEx/TopiaForge/state.json, could not be "
        'read, so nothing can be planned. Close any program using the game '
        'folder, then clean up again. If it still cannot be read, it is '
        'damaged: repair or restore that file first.',
      );
    }
    final profiles = await _loadProfiles();
    final servers = profiles.where(ServerProfiles.isServerProfile).toList();
    final removing = [
      for (final server in servers)
        if (scope == null || scope.contains(server.id)) server,
    ];
    final removingIds = {for (final server in removing) server.id};
    // What a game started without the launcher loads is the player's too.
    final used = <String>{
      for (final record in state.values)
        if (record['enabled'] == true &&
            record['uninstallPending'] != true &&
            record['id'] is String &&
            record['version'] is String)
          _versionKey(record['id'] as String, record['version'] as String),
    };
    for (final profile in profiles) {
      if (ServerProfiles.isServerProfile(profile)) continue;
      for (final pin in profile.selectedVersions.entries) {
        used.add(_versionKey(pin.key, pin.value));
      }
      final selection = await _effectiveLaunchSelection(install, profile);
      for (final mod in selection.selected) {
        if (!mod.uninstallPending) used.add(_versionKey(mod.id, mod.version));
      }
    }
    final packagesRoot = _packagesRoot(install);
    final versions = <ServerContentVersion>[];
    final removable = <ServerContentEntry>[];
    final stale = <ServerContentEntry>[];
    for (final entry in load.record.entries) {
      final folder = Directory(
        p.join(packagesRoot.path, entry.modId, entry.version),
      );
      final judged = await _judgeRecorded(folder, entry.receiptSha256);
      if (judged == null) continue;
      if (!judged) {
        stale.add(entry);
        continue;
      }
      final pins = [
        for (final server in servers)
          if (_pinsVersion(server, entry)) server.id,
      ];
      final kept = used.contains(_versionKey(entry.modId, entry.version))
          ? ServerContentKept.usedByProfile
          : pins.any((id) => !removingIds.contains(id))
          ? ServerContentKept.pinnedByKeptServer
          : null;
      versions.add(
        ServerContentVersion(
          modId: entry.modId,
          version: entry.version,
          sha256: entry.sha256,
          receiptSha256: entry.receiptSha256,
          sizeBytes: _folderBytes(folder),
          serverProfileIds: pins,
          kept: kept,
        ),
      );
      if (kept == null) removable.add(entry);
    }
    final staleWithheld = <WithheldServerVersion>[
      for (final item in load.record.withheld)
        if (await _judgeRecorded(
              Directory(p.join(packagesRoot.path, item.modId, item.version)),
              item.receiptSha256,
            ) ==
            false)
          item,
    ];
    return (
      plan: ServerContentCleanupPlan(
        versions: versions,
        profiles: [
          for (final server in removing)
            ServerContentProfile(
              id: server.id,
              name: server.name,
              revision: server.revision,
            ),
        ],
        scope: scope,
      ),
      removable: removable,
      record: load.record,
      stale: stale,
      staleWithheld: staleWithheld,
      staleAdded: _uninstalledAddedRecords(
        packagesRoot,
        load.record,
        state.keys,
      ),
    );
  }

  Future<ServerContentCleanupResult> _applyServerContentCleanup(
    GameInstall install,
    ServerContentCleanupPlan plan,
  ) async {
    final LaunchStagingLease? lease;
    try {
      lease = await LaunchStagingStore(install.path).acquireLaunchLease();
    } on Object catch (error) {
      await _appendLauncherLogBestEffort(
        'Server content cleanup could not take the staging lease: $error',
      );
      return _cleanupRefused(
        ServerContentCleanupRefusal.busy,
        'The game folder could not be prepared for the cleanup. Check that it '
        'still exists and can be written to, then try again.',
      );
    }
    if (lease == null) {
      return _cleanupRefused(
        ServerContentCleanupRefusal.busy,
        'A launch or an install is using the game folder right now. Try again '
        'when it finishes.',
      );
    }
    try {
      if (await _runningForLaunch(install)) {
        return _cleanupRefused(
          ServerContentCleanupRefusal.gameRunning,
          'Robotopia is running. Close the game, then clean up again.',
        );
      }
      // A damaged record is repaired and a join install that stopped part
      // way is settled first, so the plan is checked against what is really
      // there.
      await _repairServerContent(install);
      await _sweepJoinInstalls(install);
      final _ServerContentWork work;
      try {
        work = await _serverContentWork(install, scope: plan.scope);
      } on StateError catch (error) {
        return _cleanupRefused(
          ServerContentCleanupRefusal.unreadable,
          error.message,
        );
      } on Object catch (error) {
        await _appendLauncherLogBestEffort(
          'Server content cleanup could not be planned again: $error',
        );
        return _cleanupRefused(
          ServerContentCleanupRefusal.unreadable,
          'Your profiles or the game\'s list of mods could not be read just '
          'now. Try again in a moment.',
        );
      }
      if (work.plan.fingerprint != plan.fingerprint) {
        return _cleanupRefused(
          ServerContentCleanupRefusal.stale,
          'Something changed since this cleanup was prepared. Review it '
          'again.',
        );
      }
      return await _removeServerContent(install, work);
    } finally {
      await lease.release();
    }
  }

  /// Removes, after a join, the versions of server content no profile pins
  /// any more, such as the ones a re-join's pins replaced, under the same
  /// rules as a cleanup of no server at all. Only when no launch or install
  /// holds the game's files and the game is not running; tidying, so it never
  /// throws.
  Future<void> _removeUnpinnedServerContent(GameInstall install) async {
    try {
      final lease = await LaunchStagingStore(install.path).acquireLaunchLease();
      if (lease == null) return;
      try {
        if (await _runningForLaunch(install)) return;
        await _repairServerContent(install);
        final work = await _serverContentWork(install, scope: const {});
        if (work.removable.isEmpty &&
            work.stale.isEmpty &&
            work.staleWithheld.isEmpty &&
            work.staleAdded.isEmpty) {
          return;
        }
        final result = await _removeServerContent(install, work);
        if (!result.applied || result.message.isNotEmpty) {
          await _appendLauncherLogBestEffort(
            'Removing server content nothing pins was not finished: '
            '${result.message}',
          );
        }
      } finally {
        await lease.release();
      }
    } on Object catch (error) {
      await _appendLauncherLogBestEffort(
        'Removing server content nothing pins failed: $error',
      );
    }
  }

  Future<ServerContentCleanupResult> _removeServerContent(
    GameInstall install,
    _ServerContentWork work,
  ) async {
    final packagesRoot = _packagesRoot(install);
    // Every version leaves the packages folder first, in one pass that is
    // undone if any of them cannot move, so nothing is ever half removed.
    final trash = (_managerStaging(
      install,
    )..createSync(recursive: true)).createTempSync('launcher-trash-');
    final moved = <(Directory, Directory)>[];
    try {
      for (final entry in work.removable) {
        final folder = Directory(
          p.join(packagesRoot.path, entry.modId, entry.version),
        );
        final aside = Directory(p.join(trash.path, '${moved.length}'));
        folder.renameSync(aside.path);
        moved.add((folder, aside));
      }
      // What the record names that is no longer there goes with them.
      await _serverContentStore(install).update(
        (record) => record.without(
          [...work.removable, ...work.stale],
          addedRecords: work.staleAdded,
          unwithheld: work.staleWithheld,
        ),
      );
    } on Object catch (error) {
      for (final (folder, aside) in moved.reversed) {
        try {
          aside.renameSync(folder.path);
        } on Object {
          // The next plan lists what is still in place.
        }
      }
      _deleteJoinFolder(trash);
      await _appendLauncherLogBestEffort(
        'Server content cleanup moved nothing: $error',
      );
      return _cleanupRefused(
        ServerContentCleanupRefusal.busy,
        "A server mod's files are in use, so nothing was removed. Close any "
        'program using the game folder, then try again.',
      );
    }
    final problems = <String>[];
    final emptied = <String>{};
    for (final entry in work.removable) {
      final folder = Directory(p.join(packagesRoot.path, entry.modId));
      if (!_hasInstalledVersion(install, entry.modId)) {
        emptied.add(entry.modId);
        try {
          if (folder.existsSync()) folder.deleteSync(recursive: true);
        } on FileSystemException {
          // An empty folder left behind holds no version.
        }
      }
    }
    try {
      await _forgetUninstalledMods(install, emptied, work.record);
    } on Object catch (error) {
      // A disabled record of a mod with no version loads nothing.
      await _appendLauncherLogBestEffort(
        'Server content cleanup kept some manager records: $error',
      );
    }
    final removedProfiles = <String>[];
    try {
      removedProfiles.addAll(await _removeServerProfiles(work.plan.profiles));
    } on Object catch (error) {
      await _appendLauncherLogBestEffort(
        'Server content cleanup kept the server profiles: $error',
      );
      problems.add(
        'The mods were removed, but the server profiles could not be. Remove '
        'them in Profiles; joining a server again sets its profile up anew.',
      );
    }
    var freed = work.plan.removableBytes;
    _deleteJoinFolder(trash);
    freed += await _deleteUnusedCachedArchives(install, {
      for (final entry in work.removable) entry.sha256,
    });
    await _appendLauncherLogBestEffort(
      'Removed ${work.removable.length} version(s) of server content and '
      '${removedProfiles.length} server profile(s).',
    );
    return ServerContentCleanupResult.applied(
      removedVersions: [
        for (final entry in work.removable) '${entry.modId}@${entry.version}',
      ],
      removedProfiles: removedProfiles,
      freedBytes: freed,
      message: problems.join(' '),
    );
  }

  /// Removes the manager records of [ids], mods the cleanup left with no
  /// version, when a join added them or their uninstall is pending.
  Future<void> _forgetUninstalledMods(
    GameInstall install,
    Set<String> ids,
    ServerContentRecord record,
  ) async {
    if (ids.isEmpty) return;
    final lower = {for (final id in ids) id.toLowerCase()};
    final state = await _readManagerState(install);
    final mods = state['mods'] as List;
    bool forgotten(Object? item) {
      if (item is! Map || item['id'] is! String) return false;
      final id = (item['id'] as String).toLowerCase();
      return lower.contains(id) &&
          (record.addedRecords.contains(id) ||
              item['uninstallPending'] == true);
    }

    final kept = [
      for (final item in mods)
        if (!forgotten(item)) item,
    ];
    if (kept.length != mods.length) {
      state['mods'] = kept;
      await _saveManagerState(install, state);
    }
    await _serverContentStore(
      install,
    ).update((current) => current.without(const [], addedRecords: lower));
  }

  /// Removes [planned] from the profiles, each only at the revision the plan
  /// saw, and moves the selection off a removed one. Returns their ids.
  Future<List<String>> _removeServerProfiles(
    List<ServerContentProfile> planned,
  ) async {
    if (planned.isEmpty) return const [];
    final removed = <String>[];
    await _profileMutation(() async {
      final (stored, _) = await _readProfileStore();
      final kept = [
        for (final profile in stored)
          if (!planned.any(
            (item) =>
                item.id == profile.id &&
                item.revision == profile.revision &&
                ServerProfiles.isServerProfile(profile),
          ))
            profile,
      ];
      removed.addAll(
        stored
            .where((profile) => !kept.contains(profile))
            .map((profile) => profile.id),
      );
      if (removed.isEmpty) return;
      final remaining = kept.isEmpty
          ? [LauncherProfile.defaultProfile()]
          : kept;
      await _writeProfiles(remaining);
      await _updateSettings((settings) {
        if (removed.contains(settings['selectedProfileId'])) {
          settings['selectedProfileId'] = remaining.first.id;
        }
      });
    });
    return removed;
  }

  /// Deletes the cached archives of [sha256s] that no installed version
  /// still names; returns the bytes freed.
  Future<int> _deleteUnusedCachedArchives(
    GameInstall install,
    Set<String> sha256s,
  ) async {
    final inUse = <String>{};
    try {
      for (final idFolder in _packagesRoot(
        install,
      ).listSync(followLinks: false)) {
        if (idFolder is! Directory) continue;
        for (final versionFolder in idFolder.listSync(followLinks: false)) {
          if (versionFolder is! Directory) continue;
          final sha = await _receiptArchiveSha256(versionFolder);
          if (sha != null) inUse.add(sha);
        }
      }
    } on FileSystemException {
      return 0;
    }
    var freed = 0;
    for (final sha in sha256s.difference(inUse)) {
      final cached = _joinCacheFile(sha);
      try {
        if (FileSystemEntity.typeSync(cached.path, followLinks: false) ==
            FileSystemEntityType.file) {
          final length = await cached.length();
          await cached.delete();
          freed += length;
        }
      } on FileSystemException {
        // A cached archive kept is only disk space.
      }
    }
    return freed;
  }
}
