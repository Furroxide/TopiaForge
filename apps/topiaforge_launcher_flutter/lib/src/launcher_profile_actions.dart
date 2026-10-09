part of 'launcher_bloc.dart';

extension LauncherProfileActions on LauncherBloc {
  Future<void> _onProfileSelected(
    ProfileSelected event,
    Emitter<LauncherState> emit,
  ) async {
    if (!state.profiles.any((profile) => profile.id == event.profileId)) return;
    await _persistProfiles(
      state.profiles,
      event.profileId,
      emit,
      'Profile selected.',
    );
  }

  Future<void> _onProfileLaunchRequested(
    ProfileLaunchRequested event,
    Emitter<LauncherState> emit,
  ) async {
    if (!state.profiles.any((profile) => profile.id == event.profileId)) return;
    final saved = await _persistProfiles(
      state.profiles,
      event.profileId,
      emit,
      'Profile selected.',
    );
    if (!saved) return;
    await _launchSelectedProfile(emit, restart: false);
  }

  Future<void> _onProfileCreated(
    ProfileCreated event,
    Emitter<LauncherState> emit,
  ) async {
    final id = 'profile-${DateTime.now().millisecondsSinceEpoch}';
    final profiles = [
      ...state.profiles,
      LauncherProfile(
        id: id,
        name: 'New Profile',
        enabledMods: {
          for (final mod in state.installedMods.where((mod) => mod.enabled))
            mod.id,
        },
        selectedVersions: {
          for (final mod in state.installedMods) mod.id: mod.version,
        },
      ),
    ];
    await _persistProfiles(profiles, id, emit, 'Created profile.');
  }

  Future<void> _onSelectedProfileDuplicated(
    SelectedProfileDuplicated event,
    Emitter<LauncherState> emit,
  ) async {
    final selected = state.selectedProfile;
    if (selected == null) return;
    final id = 'profile-${DateTime.now().millisecondsSinceEpoch}';
    final copy = selected.copyWith(id: id, name: '${selected.name} Copy');
    await _persistProfiles(
      [...state.profiles, copy],
      id,
      emit,
      'Duplicated profile.',
    );
  }

  Future<void> _onSelectedProfileDeleted(
    SelectedProfileDeleted event,
    Emitter<LauncherState> emit,
  ) async {
    if (state.profiles.length <= 1) {
      emit(state.copyWith(statusMessage: 'At least one profile is required.'));
      return;
    }
    final profiles = state.profiles
        .where((profile) => profile.id != state.selectedProfileId)
        .toList();
    await _persistProfiles(
      profiles,
      profiles.first.id,
      emit,
      'Deleted profile.',
    );
  }

  Future<void> _onSafeModeToggled(
    SafeModeToggled event,
    Emitter<LauncherState> emit,
  ) async {
    final selected = state.selectedProfile;
    if (selected == null) return;
    await _saveUpdatedProfile(
      selected.copyWith(
        launchSettings: selected.launchSettings.copyWith(
          safeMode: event.enabled,
        ),
      ),
      emit,
      event.enabled
          ? 'Safe mode enabled; launch will open the main menu.'
          : 'Safe mode disabled.',
    );
  }

  Future<void> _onProfileManagerStateInheritanceChanged(
    ProfileManagerStateInheritanceChanged event,
    Emitter<LauncherState> emit,
  ) async {
    final selected = state.selectedProfile;
    if (selected == null) {
      return;
    }

    final updated = selected.copyWith(
      inheritManagerModState: event.enabled,
      enabledMods: event.enabled
          ? selected.enabledMods
          : {
              for (final mod in state.installedMods)
                if (mod.enabled && !mod.uninstallPending) mod.id,
            },
      selectedVersions: event.enabled
          ? selected.selectedVersions
          : {for (final mod in state.installedMods) mod.id: mod.version},
    );
    await _saveUpdatedProfile(
      updated,
      emit,
      event.enabled
          ? 'Profile now follows global mod choices.'
          : 'Profile now has its own mod choices.',
    );
  }

  Future<void> _onProfileModSelectionChanged(
    ProfileModSelectionChanged event,
    Emitter<LauncherState> emit,
  ) async {
    final selected = state.selectedProfile;
    final installedById = {
      for (final mod in state.installedMods)
        if (!mod.uninstallPending) mod.id.toLowerCase(): mod,
    };
    final requested = installedById[event.modId.toLowerCase()];
    if (selected == null || requested == null) {
      emit(state.copyWith(statusMessage: 'That installed mod is unavailable.'));
      return;
    }

    final enabled = selected.inheritManagerModState
        ? <String>{
            for (final mod in state.installedMods)
              if (mod.enabled && !mod.uninstallPending) mod.id,
          }
        : <String>{...selected.enabledMods};
    final versions = <String, String>{...selected.selectedVersions};
    var relatedChanges = 0;
    final unresolved = <String>[];

    if (event.enabled) {
      final pending = <InstalledMod>[requested];
      final visited = <String>{};
      while (pending.isNotEmpty) {
        final mod = pending.removeLast();
        if (!visited.add(mod.id.toLowerCase())) {
          continue;
        }
        if (!_containsModId(enabled, mod.id)) {
          enabled.add(mod.id);
          if (mod.id.toLowerCase() != requested.id.toLowerCase()) {
            relatedChanges += 1;
          }
        }
        versions[mod.id] = mod.version;
        for (final dependency
            in mod.manifest?.dependencies.where((item) => !item.optional) ??
                const <ModDependency>[]) {
          final installed = installedById[dependency.id.toLowerCase()];
          if (installed == null ||
              !dependency.versionRange.allows(installed.version)) {
            unresolved.add(dependency.id);
          } else {
            pending.add(installed);
          }
        }
      }
    } else {
      final removed = <String>{requested.id.toLowerCase()};
      _removeModId(enabled, requested.id);
      var changed = true;
      while (changed) {
        changed = false;
        for (final mod in installedById.values) {
          if (!_containsModId(enabled, mod.id)) {
            continue;
          }
          final dependsOnRemoved =
              mod.manifest?.dependencies
                  .where((item) => !item.optional)
                  .any((item) => removed.contains(item.id.toLowerCase())) ??
              false;
          if (dependsOnRemoved) {
            _removeModId(enabled, mod.id);
            removed.add(mod.id.toLowerCase());
            relatedChanges += 1;
            changed = true;
          }
        }
      }
    }

    final updated = selected.copyWith(
      inheritManagerModState: false,
      enabledMods: enabled,
      selectedVersions: versions,
    );
    final action = event.enabled ? 'Enabled' : 'Disabled';
    final related = relatedChanges == 0
        ? ''
        : ' and ${event.enabled ? 'included' : 'disabled'} '
              '$relatedChanges required mod(s)';
    final warning = unresolved.isEmpty
        ? ''
        : ' Missing or incompatible: ${unresolved.toSet().join(', ')}.';
    await _saveUpdatedProfile(
      updated,
      emit,
      '$action ${requested.name}$related.$warning',
    );
  }

  Future<void> _saveUpdatedProfile(
    LauncherProfile updated,
    Emitter<LauncherState> emit,
    String message,
  ) async {
    final profiles = [
      for (final profile in state.profiles)
        if (profile.id == updated.id) updated else profile,
    ];
    await _persistProfiles(profiles, updated.id, emit, message);
  }
}

bool _containsModId(Set<String> ids, String id) =>
    ids.any((candidate) => candidate.toLowerCase() == id.toLowerCase());

void _removeModId(Set<String> ids, String id) {
  ids.removeWhere((candidate) => candidate.toLowerCase() == id.toLowerCase());
}
