part of 'launcher_bloc.dart';

extension LauncherDeveloperActions on LauncherBloc {
  Future<void> _onDeveloperWorkspaceRefreshed(
    DeveloperWorkspaceRefreshed event,
    Emitter<LauncherState> emit,
  ) async {
    final repository = _developerRepository;
    if (repository == null) {
      emit(state.copyWith(statusMessage: 'Developer tools are unavailable.'));
      return;
    }
    await _guard(emit, 'Developer workspace refreshed.', () async {
      final workspace = await repository.loadDeveloperWorkspace();
      emit(
        state.copyWith(
          isBusy: false,
          developerWorkspace: workspace,
          statusMessage: workspace.hasProject
              ? 'Developer project ready.'
              : 'No developer project found.',
        ),
      );
    });
  }

  Future<void> _onDeveloperProjectResolved(
    DeveloperProjectResolved event,
    Emitter<LauncherState> emit,
  ) async {
    final repository = _developerRepository;
    final workspace = state.developerWorkspace;
    if (repository == null || workspace?.hasProject != true) {
      emit(state.copyWith(statusMessage: 'Open a developer project first.'));
      return;
    }
    await _guard(emit, 'Developer project restored.', () async {
      final updated = await repository.resolveDeveloperProject(
        workspace!.projectRoot,
      );
      emit(
        state.copyWith(
          isBusy: false,
          developerWorkspace: updated,
          statusMessage: updated.hasBlockingIssues
              ? 'Resolve blocking developer project issues.'
              : 'Developer project restored.',
        ),
      );
    });
  }

  Future<void> _onDeveloperDoctorRequested(
    DeveloperDoctorRequested event,
    Emitter<LauncherState> emit,
  ) async {
    final repository = _developerRepository;
    if (repository == null) {
      emit(state.copyWith(statusMessage: 'Developer tools are unavailable.'));
      return;
    }
    await _guard(emit, 'Developer doctor complete.', () async {
      final report = await repository.runDoctor(
        projectPath: state.developerWorkspace?.projectRoot,
      );
      emit(
        state.copyWith(
          isBusy: false,
          developerDoctor: report,
          statusMessage: report.ok
              ? 'Developer environment looks ready.'
              : 'Developer environment has issues.',
        ),
      );
    });
  }

  Future<void> _onDeveloperSampleProjectCreated(
    DeveloperSampleProjectCreated event,
    Emitter<LauncherState> emit,
  ) async {
    final repository = _developerRepository;
    if (repository == null) {
      emit(state.copyWith(statusMessage: 'Developer tools are unavailable.'));
      return;
    }
    await _guard(emit, 'Created developer project.', () async {
      final workspace = await repository.createModProject(
        parentDirectory: repository.developerDataRoot,
        id: 'sample.creator_mod',
        name: 'Sample Creator Mod',
        includeUnityCompanion: true,
      );
      emit(
        state.copyWith(
          isBusy: false,
          developerWorkspace: workspace,
          statusMessage: 'Created ${workspace.project!.name}.',
        ),
      );
      add(const DeveloperProjectsRefreshed());
    });
  }
}
