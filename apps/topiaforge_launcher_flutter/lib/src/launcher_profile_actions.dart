part of 'launcher_bloc.dart';

extension LauncherProfileActions on LauncherBloc {
  Future<void> _onProfileSelected(
    ProfileSelected event,
    Emitter<LauncherState> emit,
  ) async {
    await _repository.saveProfiles(state.profiles, event.profileId);
    emit(
      state.copyWith(
        selectedProfileId: event.profileId,
        statusMessage: 'Profile selected.',
      ),
    );
  }

  Future<void> _onProfileLaunchRequested(
    ProfileLaunchRequested event,
    Emitter<LauncherState> emit,
  ) async {
    final install = state.gameInstall;
    LauncherProfile? profile;
    for (final candidate in state.profiles) {
      if (candidate.id == event.profileId) {
        profile = candidate;
        break;
      }
    }
    if (install == null || profile == null) {
      return;
    }
    final selected = profile;
    await _repository.saveProfiles(state.profiles, event.profileId);
    emit(state.copyWith(selectedProfileId: event.profileId));
    await _guard(emit, 'Launched TopiaForge.', () async {
      final launchInstall = await _repairRuntimeBeforeLaunchIfNeeded(
        emit,
        install,
      );
      if (launchInstall == null) {
        return;
      }
      final result = await _repository.launch(launchInstall, selected);
      emit(_launchResultState(result));
    });
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
    await _repository.saveProfiles(profiles, id);
    emit(
      state.copyWith(
        profiles: profiles,
        selectedProfileId: id,
        statusMessage: 'Created profile.',
      ),
    );
  }

  Future<void> _onSelectedProfileDuplicated(
    SelectedProfileDuplicated event,
    Emitter<LauncherState> emit,
  ) async {
    final selected = state.selectedProfile;
    if (selected == null) {
      return;
    }
    final id = 'profile-${DateTime.now().millisecondsSinceEpoch}';
    final copy = selected.copyWith(id: id, name: '${selected.name} Copy');
    final profiles = [...state.profiles, copy];
    await _repository.saveProfiles(profiles, id);
    emit(
      state.copyWith(
        profiles: profiles,
        selectedProfileId: id,
        statusMessage: 'Duplicated profile.',
      ),
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
    await _repository.saveProfiles(profiles, profiles.first.id);
    emit(
      state.copyWith(
        profiles: profiles,
        selectedProfileId: profiles.first.id,
        statusMessage: 'Deleted profile.',
      ),
    );
  }

  Future<void> _onSafeModeToggled(
    SafeModeToggled event,
    Emitter<LauncherState> emit,
  ) async {
    final selected = state.selectedProfile;
    if (selected == null) {
      return;
    }
    final updated = selected.copyWith(
      launchSettings: selected.launchSettings.copyWith(safeMode: event.enabled),
    );
    final profiles = [
      for (final profile in state.profiles)
        if (profile.id == updated.id) updated else profile,
    ];
    await _repository.saveProfiles(profiles, updated.id);
    emit(
      state.copyWith(
        profiles: profiles,
        selectedProfileId: updated.id,
        statusMessage: event.enabled
            ? 'Safe mode enabled.'
            : 'Safe mode disabled.',
      ),
    );
  }
}
