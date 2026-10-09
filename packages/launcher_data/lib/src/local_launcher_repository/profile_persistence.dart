part of '../local_launcher_repository.dart';

final Map<String, Future<void>> _profileMutationTails = {};
final Expando<Map<String, String>> _profileRemovalWitness =
    Expando<Map<String, String>>();

LauncherProfile _requireValidLauncherProfile(LauncherProfile profile) {
  LaunchStorageKeys.request(profile.id);
  if (profile.revision < 0 || profile.revision > 2147483647) {
    throw const FormatException(
      'Profile revision is outside the transport range.',
    );
  }
  final enabled = <String>{};
  for (final id in profile.enabledMods) {
    if (!ModManifest.isValidId(id) || !enabled.add(id.toLowerCase())) {
      throw const FormatException(
        'Profile enabled package ids are invalid or duplicated.',
      );
    }
  }
  final pins = <String>{};
  for (final pin in profile.selectedVersions.entries) {
    PackageIdentity(id: pin.key, version: pin.value);
    if (!pins.add(pin.key.toLowerCase())) {
      throw const FormatException('Profile package pins are duplicated.');
    }
  }
  LaunchSelection.fromJson(profile.launchSelection.toJson());
  for (final argument in profile.launchSettings.extraArguments) {
    if (argument.contains('\u0000')) {
      throw const FormatException(
        'Profile argument contains a null character.',
      );
    }
  }
  for (final entry in profile.launchSettings.environment.entries) {
    if (entry.key.isEmpty ||
        entry.key.contains('=') ||
        entry.key.contains('\u0000') ||
        entry.value.contains('\u0000')) {
      throw const FormatException('Profile environment entry is invalid.');
    }
  }
  return profile;
}

String _canonicalProfileValue(Object? value) {
  if (value is Map) {
    final keys = value.keys.cast<String>().toList()..sort();
    return '{${keys.map((key) => '${jsonEncode(key)}:${_canonicalProfileValue(value[key])}').join(',')}}';
  }
  if (value is List) {
    return '[${value.map(_canonicalProfileValue).join(',')}]';
  }
  return jsonEncode(value);
}

extension _ProfilePersistence on LocalLauncherRepository {
  Future<T> _profileMutation<T>(Future<T> Function() operation) {
    final path = p.normalize(p.absolute(_profilesFile.path));
    final key = Platform.isWindows ? path.toLowerCase() : path;
    final result = Completer<T>();
    final previous = _profileMutationTails[key] ?? Future<void>.value();
    final tail = previous.then((_) async {
      try {
        _ensureDataRoot();
        final lock = await File(
          p.join(_dataRoot.path, 'profiles.lock'),
        ).open(mode: FileMode.append);
        var locked = false;
        late T value;
        try {
          final elapsed = Stopwatch()..start();
          while (!locked) {
            try {
              await lock.lock();
              locked = true;
            } on FileSystemException {
              if (elapsed.elapsed >= const Duration(seconds: 5)) rethrow;
              await Future<void>.delayed(const Duration(milliseconds: 25));
            }
          }
          value = await operation();
        } finally {
          try {
            if (locked) await lock.unlock();
          } finally {
            await lock.close();
          }
        }
        result.complete(value);
      } on Object catch (error, stack) {
        if (!result.isCompleted) result.completeError(error, stack);
      }
    });
    _profileMutationTails[key] = tail;
    unawaited(
      tail.then((_) {
        if (identical(_profileMutationTails[key], tail)) {
          _profileMutationTails.remove(key);
        }
      }),
    );
    return result.future;
  }

  Future<(List<LauncherProfile>, bool)> _readProfileStore() async {
    await _recoverAtomicBackupIfMissing(_profilesFile);
    if (!_profilesFile.existsSync()) return (<LauncherProfile>[], true);
    final decoded = await _readJsonFileBounded(
      _profilesFile,
      maxBytes: _maxProfilesBytes,
      label: 'Launcher profiles',
      preserveNumbers: true,
    );
    if (decoded is! Map ||
        decoded['schemaVersion'] is! int ||
        !const [2, 3].contains(decoded['schemaVersion'])) {
      throw const FormatException(
        'Launcher profiles require supported schemaVersion 2 or 3.',
      );
    }
    final entries = decoded['profiles'];
    if (entries is! List) {
      throw const FormatException(
        'Launcher profiles must contain a profiles array.',
      );
    }
    final profiles = <LauncherProfile>[];
    final ids = <String>{};
    for (var index = 0; index < entries.length; index++) {
      final entry = entries[index];
      if (entry is! Map<String, Object?>) {
        throw FormatException(
          'Launcher profile at original index $index is not an object.',
        );
      }
      if (decoded['schemaVersion'] == 3 &&
          (!entry.containsKey('launchSelection') ||
              !entry.containsKey('revision'))) {
        throw FormatException(
          'Version 3 profile at index $index is missing selection or revision.',
        );
      }
      final profile = _requireValidLauncherProfile(
        LauncherProfile.fromJson(entry),
      );
      if (!ids.add(profile.id)) {
        throw FormatException(
          'Duplicate launcher profile identity ${profile.id}.',
        );
      }
      profiles.add(profile);
    }
    return (profiles, decoded['schemaVersion'] == 2);
  }

  Future<void> _writeProfiles(List<LauncherProfile> profiles) =>
      _writeJsonFileAtomic(
        _profilesFile,
        {
          'schemaVersion': _profileFormatVersion,
          'profiles': profiles.map((profile) => profile.toJson()).toList(),
        },
        maxBytes: _maxProfilesBytes,
        label: 'Launcher profiles',
      );

  Future<List<LauncherProfile>> _loadVersionedProfiles() =>
      _profileMutation(() async {
        final (stored, migrate) = await _readProfileStore();
        final profiles = stored.isEmpty
            ? [LauncherProfile.defaultProfile()]
            : stored;
        if (migrate || stored.isEmpty) await _writeProfiles(profiles);
        _recordProfileWitness(profiles);
        return List.unmodifiable(profiles);
      });

  Future<List<LauncherProfile>> _saveVersionedProfiles(
    List<LauncherProfile> source,
    String selectedProfileId,
  ) async {
    final incoming =
        (source.isEmpty ? [LauncherProfile.defaultProfile()] : source)
            .map(
              (profile) => _requireValidLauncherProfile(
                LauncherProfile.fromJson(
                  jsonDecode(jsonEncode(profile.toJson()))
                      as Map<String, Object?>,
                ),
              ),
            )
            .toList();
    return _profileMutation(() async {
      final (stored, _) = await _readProfileStore();
      final previous = {for (final profile in stored) profile.id: profile};
      final incomingIds = incoming.map((profile) => profile.id).toSet();
      final witness = _profileRemovalWitness[this] ?? const <String, String>{};
      for (final removed in stored.where(
        (profile) => !incomingIds.contains(profile.id),
      )) {
        if (witness[removed.id] != _canonicalProfileValue(removed.toJson())) {
          throw StateError(
            'Profile ${removed.id} was added or changed on disk. Reload the profile list before deleting it.',
          );
        }
      }
      for (final restored in incoming.where(
        (profile) => !previous.containsKey(profile.id),
      )) {
        if (witness.containsKey(restored.id)) {
          throw StateError(
            'Profile ${restored.id} was removed on disk. Reload the profile list before saving.',
          );
        }
      }
      final ids = <String>{};
      final result = <LauncherProfile>[];
      for (final profile in incoming) {
        if (!ids.add(profile.id)) {
          throw FormatException(
            'Duplicate launcher profile identity ${profile.id}.',
          );
        }
        final old = previous[profile.id];
        var revision = 0;
        if (old != null) {
          final original = old.toJson()..remove('revision');
          final replacement = profile.toJson()..remove('revision');
          final changed =
              _canonicalProfileValue(original) !=
              _canonicalProfileValue(replacement);
          if (changed && profile.revision != old.revision) {
            throw StateError(
              'Profile ${profile.id} changed on disk. Reload it before editing.',
            );
          }
          if (changed && old.revision == 2147483647) {
            throw StateError(
              'Profile revision limit reached; duplicate the profile with a new identity.',
            );
          }
          revision = old.revision + (changed ? 1 : 0);
        }
        result.add(profile.copyWith(revision: revision));
      }
      if (!ids.contains(selectedProfileId)) {
        throw const FormatException(
          'Selected profile is not in the saved profile set.',
        );
      }
      await _writeProfiles(result);
      await _updateSettings(
        (settings) => settings['selectedProfileId'] = selectedProfileId,
      );
      _recordProfileWitness(result);
      return List.unmodifiable(result);
    });
  }

  void _recordProfileWitness(List<LauncherProfile> profiles) {
    _profileRemovalWitness[this] = Map.unmodifiable({
      for (final profile in profiles)
        profile.id: _canonicalProfileValue(profile.toJson()),
    });
  }

  Future<void> _requireCurrentProfileRevision(
    LauncherProfile profile,
  ) => _profileMutation(() async {
    final (stored, _) = await _readProfileStore();
    final current = stored.where((item) => item.id == profile.id).firstOrNull;
    if (current == null &&
        (_profileRemovalWitness[this]?.containsKey(profile.id) ?? false)) {
      throw StateError(
        'Profile ${profile.id} was removed on disk during preflight. Reload before launching.',
      );
    }
    if (current != null && current.revision != profile.revision) {
      throw StateError(
        'Profile ${profile.id} changed on disk during preflight. Reload it before launching.',
      );
    }
  });

  Future<LauncherProfile> _importVersionedProfile(String path) async {
    _requireProfileExportPath(path);
    final decoded = await _readJsonFileBounded(
      File(path),
      maxBytes: _maxProfilesBytes,
      label: 'Imported launcher profile',
      preserveNumbers: true,
    );
    if (decoded is! Map ||
        decoded['schemaVersion'] is! int ||
        !const [2, 3].contains(decoded['schemaVersion'])) {
      throw const FormatException(
        'Imported launcher profile requires supported schemaVersion 2 or 3.',
      );
    }
    final raw = decoded['profile'];
    if (raw is! Map<String, Object?>) {
      throw const FormatException(
        'Imported launcher profile is missing its profile object.',
      );
    }
    if (decoded['schemaVersion'] == 3 &&
        (!raw.containsKey('launchSelection') || !raw.containsKey('revision'))) {
      throw const FormatException(
        'Version 3 profile is missing selection or revision.',
      );
    }
    return _requireValidLauncherProfile(LauncherProfile.fromJson(raw));
  }
}
