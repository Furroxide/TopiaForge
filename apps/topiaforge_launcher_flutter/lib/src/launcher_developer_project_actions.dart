part of 'launcher_bloc.dart';

extension LauncherDeveloperProjectActions on LauncherBloc {
  Future<void> _onDeveloperEnvironmentChecked(
    DeveloperEnvironmentChecked event,
    Emitter<LauncherState> emit,
  ) async {
    final repository = _developerRepository;
    if (repository == null) {
      emit(state.copyWith(statusMessage: 'Developer tools are unavailable.'));
      return;
    }
    await _guard(emit, 'Checked developer environment.', () async {
      final env = await repository.checkEnvironment();
      emit(
        state.copyWith(
          isBusy: false,
          developerEnvironment: env,
          statusMessage: env.developerReady
              ? 'Developer toolchain ready.'
              : 'Developer setup needed — see Environment.',
        ),
      );
    });
  }

  Future<void> _onDeveloperSetupRequested(
    DeveloperSetupRequested event,
    Emitter<LauncherState> emit,
  ) async {
    final repository = _developerRepository;
    if (repository == null) {
      emit(state.copyWith(statusMessage: 'Developer tools are unavailable.'));
      return;
    }
    await _guard(emit, 'Setup complete.', () async {
      final result = await repository.runSetup();
      emit(
        state.copyWith(
          isBusy: false,
          developerEnvironment: result.environment,
          developerSetup: result,
          statusMessage: result.actions.isEmpty
              ? 'Setup complete.'
              : result.actions.last,
        ),
      );
    });
  }

  Future<void> _onDeveloperProjectPacked(
    DeveloperProjectPacked event,
    Emitter<LauncherState> emit,
  ) async {
    final repository = _developerRepository;
    final workspace = state.developerWorkspace;
    if (repository == null || workspace?.hasProject != true) {
      emit(state.copyWith(statusMessage: 'Open a developer project first.'));
      return;
    }
    await _guard(emit, 'Packed project.', () async {
      final path = await repository.packProject(workspace!.projectRoot);
      await _repository.openContainingFolder(path);
      emit(state.copyWith(isBusy: false, statusMessage: 'Packed to $path.'));
    });
  }

  Future<void> _onDeveloperProjectInstalledToGame(
    DeveloperProjectInstalledToGame event,
    Emitter<LauncherState> emit,
  ) async {
    final repository = _developerRepository;
    final workspace = state.developerWorkspace;
    final install = state.gameInstall;
    if (repository == null || workspace?.hasProject != true) {
      emit(state.copyWith(statusMessage: 'Open a developer project first.'));
      return;
    }
    if (install == null) {
      emit(state.copyWith(statusMessage: 'Detect a Robotopia install first.'));
      return;
    }
    await _guard(emit, 'Installed project into the game.', () async {
      final path = await repository.packProject(workspace!.projectRoot);
      await _repository.installPackage(path, install);
      emit(
        _snapshotState(
          await _repository.loadSnapshot(),
          'Installed ${workspace.project!.name} into the game.',
        ),
      );
    });
  }

  Future<void> _onDeveloperProjectFolderOpened(
    DeveloperProjectFolderOpened event,
    Emitter<LauncherState> emit,
  ) async {
    final workspace = state.developerWorkspace;
    if (workspace?.hasProject != true) {
      emit(state.copyWith(statusMessage: 'Open a developer project first.'));
      return;
    }
    await _repository.openPath(workspace!.projectRoot);
    emit(state.copyWith(statusMessage: 'Opened ${workspace.projectRoot}.'));
  }

  Future<void> _onDeveloperToolLinkOpened(
    DeveloperToolLinkOpened event,
    Emitter<LauncherState> emit,
  ) async {
    if (event.url.isEmpty) {
      return;
    }
    await _repository.openPath(event.url);
    emit(state.copyWith(statusMessage: 'Opened ${event.url}'));
  }

  Future<void> _onDeveloperModProjectCreated(
    DeveloperModProjectCreated event,
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
        id: event.id,
        name: event.name,
        includeUnityCompanion: event.includeUnityCompanion,
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

  Future<void> _onDeveloperProjectsRefreshed(
    DeveloperProjectsRefreshed event,
    Emitter<LauncherState> emit,
  ) async {
    final repository = _developerRepository;
    if (repository == null) {
      return;
    }
    try {
      final projects = await repository.listProjects();
      final editors = await repository.listUnityEditors();
      emit(state.copyWith(developerProjects: projects, unityEditors: editors));
    } on Object catch (error) {
      emit(state.copyWith(statusMessage: 'Could not list projects: $error'));
    }
  }

  Future<void> _onDeveloperProjectAdded(
    DeveloperProjectAdded event,
    Emitter<LauncherState> emit,
  ) async {
    final repository = _developerRepository;
    if (repository == null) {
      return;
    }
    await _guard(emit, 'Tracked project.', () async {
      final projects = await repository.addExistingProject(event.path);
      emit(
        state.copyWith(
          isBusy: false,
          developerProjects: projects,
          statusMessage: 'Tracked ${event.path}.',
        ),
      );
    });
  }

  Future<void> _onDeveloperProjectRemoved(
    DeveloperProjectRemoved event,
    Emitter<LauncherState> emit,
  ) async {
    final repository = _developerRepository;
    if (repository == null) {
      return;
    }
    await _guard(emit, 'Removed project.', () async {
      final projects = await repository.removeProject(event.path);
      emit(
        state.copyWith(
          isBusy: false,
          developerProjects: projects,
          statusMessage: 'Untracked ${event.path}.',
        ),
      );
    });
  }

  Future<void> _onDeveloperProjectOpenedInUnity(
    DeveloperProjectOpenedInUnity event,
    Emitter<LauncherState> emit,
  ) async {
    final repository = _developerRepository;
    if (repository == null) {
      return;
    }
    await _guard(emit, 'Opened project in Unity.', () async {
      final editor = await repository.openProjectInUnity(event.path);
      final projects = await repository.listProjects();
      emit(
        state.copyWith(
          isBusy: false,
          developerProjects: projects,
          statusMessage: 'Opened ${event.path} in Unity ($editor).',
        ),
      );
    });
  }

  Future<void> _onDeveloperProjectManaged(
    DeveloperProjectManaged event,
    Emitter<LauncherState> emit,
  ) async {
    final repository = _developerRepository;
    if (repository == null) {
      return;
    }
    await _guard(emit, 'Loaded project.', () async {
      final projects = await repository.touchProjectOpened(event.path);
      RegisteredProject? managed;
      for (final project in projects) {
        if (_samePath(project.path, event.path)) {
          managed = project;
          break;
        }
      }

      if (managed != null && managed.isUnity) {
        final resolved = await repository.resolveUnityProject(
          event.path,
          restore: false,
        );
        final available = await repository.listAvailableUnityPackages();
        final repos = await repository.listUnityRepos();
        emit(
          state.copyWith(
            isBusy: false,
            managedProject: managed,
            developerProjects: projects,
            unityResolved: resolved,
            unityAvailable: available,
            unityRepos: repos,
            statusMessage: 'Managing ${managed.name} (Unity).',
          ),
        );
        return;
      }

      final workspace = await repository.loadDeveloperWorkspace(
        projectPath: event.path,
      );
      emit(
        state.copyWith(
          isBusy: false,
          developerWorkspace: workspace,
          developerProjects: projects,
          managedProject: managed,
          clearManagedProject: managed == null,
          statusMessage: workspace.hasProject
              ? 'Managing ${workspace.project!.name}.'
              : 'Opened ${event.path}.',
        ),
      );
    });
  }

  bool _samePath(String a, String b) {
    String norm(String s) =>
        s.replaceAll('\\', '/').replaceAll(RegExp(r'/+$'), '').toLowerCase();
    return norm(a) == norm(b);
  }

  Future<void> _onDeveloperUnityResolved(
    DeveloperUnityResolved event,
    Emitter<LauncherState> emit,
  ) async {
    final repository = _developerRepository;
    final managed = state.managedProject;
    if (repository == null || managed == null) {
      return;
    }
    await _guard(emit, 'Resolved Unity packages.', () async {
      final resolved = await repository.resolveUnityProject(
        managed.path,
        restore: true,
      );
      emit(
        state.copyWith(
          isBusy: false,
          unityResolved: resolved,
          statusMessage: 'Resolved ${resolved.length} package(s).',
        ),
      );
    });
  }

  Future<void> _onDeveloperUnityPackageAdded(
    DeveloperUnityPackageAdded event,
    Emitter<LauncherState> emit,
  ) async {
    final repository = _developerRepository;
    final managed = state.managedProject;
    if (repository == null || managed == null) {
      return;
    }
    await _guard(emit, 'Added package.', () async {
      final resolved = await repository.addUnityPackage(
        managed.path,
        event.id,
        event.versionRange,
      );
      emit(
        state.copyWith(
          isBusy: false,
          unityResolved: resolved,
          statusMessage: 'Added ${event.id}.',
        ),
      );
    });
  }

  Future<void> _onDeveloperUnityPackageRemoved(
    DeveloperUnityPackageRemoved event,
    Emitter<LauncherState> emit,
  ) async {
    final repository = _developerRepository;
    final managed = state.managedProject;
    if (repository == null || managed == null) {
      return;
    }
    await _guard(emit, 'Removed package.', () async {
      final resolved = await repository.removeUnityPackage(
        managed.path,
        event.id,
      );
      emit(
        state.copyWith(
          isBusy: false,
          unityResolved: resolved,
          statusMessage: 'Removed ${event.id}.',
        ),
      );
    });
  }

  Future<void> _onDeveloperUnityRepoAdded(
    DeveloperUnityRepoAdded event,
    Emitter<LauncherState> emit,
  ) async {
    final repository = _developerRepository;
    if (repository == null) {
      return;
    }
    await _guard(emit, 'Added repository.', () async {
      final repos = await repository.addUnityRepo(event.url);
      final available = await repository.listAvailableUnityPackages();
      emit(
        state.copyWith(
          isBusy: false,
          unityRepos: repos,
          unityAvailable: available,
          statusMessage: 'Subscribed to ${event.url}.',
        ),
      );
    });
  }

  Future<void> _onDeveloperUnityRepoRemoved(
    DeveloperUnityRepoRemoved event,
    Emitter<LauncherState> emit,
  ) async {
    final repository = _developerRepository;
    if (repository == null) {
      return;
    }
    await _guard(emit, 'Removed repository.', () async {
      final repos = await repository.removeUnityRepo(event.id);
      final available = await repository.listAvailableUnityPackages();
      emit(
        state.copyWith(
          isBusy: false,
          unityRepos: repos,
          unityAvailable: available,
          statusMessage: 'Unsubscribed.',
        ),
      );
    });
  }

  Future<void> _onDeveloperUnityProjectCreated(
    DeveloperUnityProjectCreated event,
    Emitter<LauncherState> emit,
  ) async {
    final repository = _developerRepository;
    if (repository == null) {
      return;
    }
    await _guard(emit, 'Created Unity project.', () async {
      final projects = await repository.createUnityProject(
        parentDirectory: repository.developerDataRoot,
        name: event.name,
        template: event.template,
      );
      final editors = await repository.listUnityEditors();
      emit(
        state.copyWith(
          isBusy: false,
          developerProjects: projects,
          unityEditors: editors,
          statusMessage: 'Created Unity world project ${event.name}.',
        ),
      );
    });
  }
}
