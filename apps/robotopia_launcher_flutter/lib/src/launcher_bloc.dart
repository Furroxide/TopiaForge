import 'dart:async';
import 'dart:convert';
import 'dart:io';

import 'package:flutter_bloc/flutter_bloc.dart';
import 'package:launcher_domain/launcher_domain.dart';

import 'launcher_event.dart';
import 'launcher_section.dart';
import 'launcher_state.dart';

part 'launcher_bloc_actions.dart';
part 'launcher_game_install_actions.dart';
part 'launcher_profile_actions.dart';
part 'launcher_developer_ugc_actions.dart';
part 'launcher_developer_project_actions.dart';
part 'launcher_developer_actions.dart';

class LauncherBloc extends Bloc<LauncherEvent, LauncherState> {
  LauncherBloc(this._repository, {DeveloperRepository? developerRepository})
    : _developerRepository = developerRepository,
      super(LauncherState.initial()) {
    on<LauncherStarted>(_onLoad);
    on<LauncherRefreshRequested>(_onLoad);
    on<LauncherSectionSelected>(_onSectionSelected);
    on<ModSelected>(_onModSelected);
    on<ModSearchChanged>(_onModSearchChanged);
    on<ProfileSelected>(_onProfileSelected);
    on<ProfileLaunchRequested>(_onProfileLaunchRequested);
    on<ProfileCreated>(_onProfileCreated);
    on<SelectedProfileDuplicated>(_onSelectedProfileDuplicated);
    on<SelectedProfileDeleted>(_onSelectedProfileDeleted);
    on<SafeModeToggled>(_onSafeModeToggled);
    on<WorldSelectionChanged>(_onWorldSelectionChanged);
    on<KnownInstallDetected>(_onKnownInstallDetected);
    on<GameDirectorySelected>(_onGameDirectorySelected);
    on<RuntimeRepaired>(_onRuntimeRepaired);
    on<PackagePreviewRequested>(_onPackagePreviewRequested);
    on<PreviewedPackageInstalled>(_onPreviewedPackageInstalled);
    on<InboxPackagesInstalled>(_onInboxPackagesInstalled);
    on<SelectedModEnabledChanged>(_onSelectedModEnabledChanged);
    on<AllModsDisabled>(_onAllModsDisabled);
    on<SelectedModUninstalled>(_onSelectedModUninstalled);
    on<GameLaunchRequested>(_onGameLaunchRequested);
    on<GameRestartRequested>(_onGameRestartRequested);
    on<DiagnosticBundleRequested>(_onDiagnosticBundleRequested);
    on<RecheckGameCompatRequested>(_onRecheckGameCompat);
    on<SelectedProfileExported>(_onSelectedProfileExported);
    on<ProfileImported>(_onProfileImported);
    on<PackageSourceAdded>(_onPackageSourceAdded);
    on<PackageSourceEnabledChanged>(_onPackageSourceEnabledChanged);
    on<PackageSourceRemoved>(_onPackageSourceRemoved);
    on<PackageSourcesRefreshed>(_onLoad);
    on<LauncherUpdateSettingsChanged>(_onLauncherUpdateSettingsChanged);
    on<GameFolderOpened>(_onGameFolderOpened);
    on<DataFolderOpened>(_onDataFolderOpened);
    on<DeveloperWorkspaceRefreshed>(_onDeveloperWorkspaceRefreshed);
    on<DeveloperProjectResolved>(_onDeveloperProjectResolved);
    on<DeveloperDoctorRequested>(_onDeveloperDoctorRequested);
    on<DeveloperSampleProjectCreated>(_onDeveloperSampleProjectCreated);
    on<DeveloperUgcSettingsSaved>(_onDeveloperUgcSettingsSaved);
    on<DeveloperUgcConfigDeployed>(_onDeveloperUgcConfigDeployed);
    on<DeveloperWatchFolderOpened>(_onDeveloperWatchFolderOpened);
    on<DeveloperUgcPublishToggled>(_onDeveloperUgcPublishToggled);
    on<DeveloperUgcStatusRefreshed>(_onDeveloperUgcStatusRefreshed);
    on<DeveloperUgcGoLive>(_onDeveloperUgcGoLive);
    on<DeveloperUgcSidecarOutput>(_onDeveloperUgcSidecarOutput);
    on<DeveloperModeToggled>(_onDeveloperModeToggled);
    on<DeveloperEnvironmentChecked>(_onDeveloperEnvironmentChecked);
    on<DeveloperSetupRequested>(_onDeveloperSetupRequested);
    on<DeveloperProjectPacked>(_onDeveloperProjectPacked);
    on<DeveloperProjectInstalledToGame>(_onDeveloperProjectInstalledToGame);
    on<DeveloperProjectFolderOpened>(_onDeveloperProjectFolderOpened);
    on<DeveloperToolLinkOpened>(_onDeveloperToolLinkOpened);
    on<DeveloperModProjectCreated>(_onDeveloperModProjectCreated);
    on<DeveloperProjectsRefreshed>(_onDeveloperProjectsRefreshed);
    on<DeveloperProjectAdded>(_onDeveloperProjectAdded);
    on<DeveloperProjectRemoved>(_onDeveloperProjectRemoved);
    on<DeveloperProjectOpenedInUnity>(_onDeveloperProjectOpenedInUnity);
    on<DeveloperProjectManaged>(_onDeveloperProjectManaged);
    on<DeveloperUnityProjectCreated>(_onDeveloperUnityProjectCreated);
    on<DeveloperUnityResolved>(_onDeveloperUnityResolved);
    on<DeveloperUnityPackageAdded>(_onDeveloperUnityPackageAdded);
    on<DeveloperUnityPackageRemoved>(_onDeveloperUnityPackageRemoved);
    on<DeveloperUnityRepoAdded>(_onDeveloperUnityRepoAdded);
    on<DeveloperUnityRepoRemoved>(_onDeveloperUnityRepoRemoved);
  }

  final LauncherRepository _repository;
  final DeveloperRepository? _developerRepository;
  final DependencyPlanner _dependencyPlanner = const DependencyPlanner();

  // The Automerge publisher (Node sidecar) when running in watch mode from the launcher.
  Process? _ugcPublisher;

  // The publisher's piped stdout/stderr line subscriptions, tracked so they can be cancelled when the publisher
  // stops or the bloc closes (otherwise they could call add() on a closed bloc).
  StreamSubscription<String>? _ugcStdoutSub;
  StreamSubscription<String>? _ugcStderrSub;

  // True while a "Go Live" is waiting for the publisher to report its live document URL before launching the game
  // (so the game auto-connects to the real document, not an empty one).
  bool _ugcGoLivePending = false;

  String get dataRoot => _repository.dataRoot;

  // Cancels and clears the publisher stream subscriptions. Safe to call repeatedly.
  void _cancelUgcPublisherStreams() {
    _ugcStdoutSub?.cancel();
    _ugcStderrSub?.cancel();
    _ugcStdoutSub = null;
    _ugcStderrSub = null;
  }

  @override
  Future<void> close() async {
    _cancelUgcPublisherStreams();
    _ugcPublisher?.kill();
    _ugcPublisher = null;
    return super.close();
  }

  Future<void> _onLoad(LauncherEvent event, Emitter<LauncherState> emit) async {
    await _guard(emit, 'Refreshed launcher state.', () async {
      final snapshot = await _repository.loadSnapshot();
      emit(_snapshotState(snapshot, 'Ready.'));
    });
  }

  void _onSectionSelected(
    LauncherSectionSelected event,
    Emitter<LauncherState> emit,
  ) {
    emit(state.copyWith(section: event.section));
    // Auto-populate the Developer cockpit the first time it's opened.
    if (event.section == LauncherSection.developer &&
        _developerRepository != null) {
      if (state.developerEnvironment == null) {
        add(const DeveloperEnvironmentChecked());
      }
      if (state.developerWorkspace == null) {
        add(const DeveloperWorkspaceRefreshed());
      }
      if (state.developerProjects.isEmpty) {
        add(const DeveloperProjectsRefreshed());
      }
    }
  }

  Future<void> _onDeveloperModeToggled(
    DeveloperModeToggled event,
    Emitter<LauncherState> emit,
  ) async {
    await _repository.setDeveloperMode(event.enabled);
    // Don't strand the user on a now-hidden tab.
    final section = !event.enabled && state.section == LauncherSection.developer
        ? LauncherSection.home
        : state.section;
    emit(
      state.copyWith(
        developerMode: event.enabled,
        section: section,
        statusMessage: event.enabled
            ? 'Developer mode enabled.'
            : 'Developer mode disabled.',
      ),
    );
    if (event.enabled &&
        _developerRepository != null &&
        state.developerEnvironment == null) {
      add(const DeveloperEnvironmentChecked());
    }
  }

  Future<void> _onLauncherUpdateSettingsChanged(
    LauncherUpdateSettingsChanged event,
    Emitter<LauncherState> emit,
  ) async {
    final settings = state.launcherUpdates.copyWith(
      enabled: event.enabled,
      checkAutomatically: event.checkAutomatically,
      channel: event.channel,
    );
    await _repository.saveLauncherUpdateSettings(settings);
    emit(
      state.copyWith(
        launcherUpdates: settings,
        statusMessage:
            'Launcher updates set to ${settings.channel.name} channel.',
      ),
    );
  }

  void _onModSelected(ModSelected event, Emitter<LauncherState> emit) {
    emit(state.copyWith(selectedModId: event.modId));
  }

  void _onModSearchChanged(
    ModSearchChanged event,
    Emitter<LauncherState> emit,
  ) {
    emit(state.copyWith(modSearch: event.query));
  }

  Future<void> _onPackagePreviewRequested(
    PackagePreviewRequested event,
    Emitter<LauncherState> emit,
  ) async {
    final install = state.gameInstall;
    if (install == null) {
      return;
    }
    await _guard(emit, 'Package plan ready.', () async {
      final plan = await _repository.previewPackage(
        event.packagePath,
        install,
        expectedSha256: event.expectedSha256,
        sourceId: event.sourceId,
        sourceName: event.sourceName,
      );
      emit(
        state.copyWith(
          isBusy: false,
          previewedPackagePath: event.packagePath,
          previewedPackageSha256: event.expectedSha256,
          installPlan: plan,
          statusMessage: plan.hasBlockingIssues
              ? 'Package has blocking dependency or conflict issues.'
              : 'Package plan is clean. Review it before installing.',
          clearError: true,
        ),
      );
    });
  }

  Future<void> _onPreviewedPackageInstalled(
    PreviewedPackageInstalled event,
    Emitter<LauncherState> emit,
  ) async {
    final install = state.gameInstall;
    final packagePath = state.previewedPackagePath;
    if (install == null || packagePath == null) {
      return;
    }
    if (state.installPlan?.hasBlockingIssues == true) {
      emit(
        state.copyWith(
          statusMessage: 'Resolve blocking issues before install.',
        ),
      );
      return;
    }
    await _guard(emit, 'Installed package.', () async {
      await _repository.installPackage(
        packagePath,
        install,
        expectedSha256: state.previewedPackageSha256,
      );
      emit(
        _snapshotState(
          await _repository.loadSnapshot(),
          'Installed ${state.installPlan?.manifest.name ?? 'package'}.',
          selectedModId: state.installPlan?.manifest.id,
        ).copyWith(clearInstallPlan: true, clearPreview: true),
      );
    });
  }

  Future<void> _onInboxPackagesInstalled(
    InboxPackagesInstalled event,
    Emitter<LauncherState> emit,
  ) async {
    final install = state.gameInstall;
    if (install == null) {
      return;
    }
    await _guard(emit, 'Installed inbox packages.', () async {
      await _repository.installInboxPackages(install);
      emit(
        _snapshotState(
          await _repository.loadSnapshot(),
          'Processed package inbox.',
        ),
      );
    });
  }

  Future<void> _onSelectedModEnabledChanged(
    SelectedModEnabledChanged event,
    Emitter<LauncherState> emit,
  ) async {
    final install = state.gameInstall;
    final mod = state.selectedMod;
    if (install == null || mod == null) {
      return;
    }
    await _guard(
      emit,
      event.enabled ? 'Enabled mod.' : 'Disabled mod.',
      () async {
        await _repository.setModEnabled(install, mod.id, event.enabled);
        emit(
          _snapshotState(
            await _repository.loadSnapshot(),
            '${event.enabled ? 'Enabled' : 'Disabled'} ${mod.name}.',
          ),
        );
      },
    );
  }

  Future<void> _onAllModsDisabled(
    AllModsDisabled event,
    Emitter<LauncherState> emit,
  ) async {
    final install = state.gameInstall;
    if (install == null) {
      return;
    }
    await _guard(emit, 'Disabled all mods.', () async {
      await _repository.disableAllMods(install);
      emit(
        _snapshotState(await _repository.loadSnapshot(), 'Disabled all mods.'),
      );
    });
  }

  Future<void> _onSelectedModUninstalled(
    SelectedModUninstalled event,
    Emitter<LauncherState> emit,
  ) async {
    final install = state.gameInstall;
    final mod = state.selectedMod;
    if (install == null || mod == null) {
      return;
    }
    await _guard(emit, 'Uninstalled mod.', () async {
      await _repository.uninstallMod(install, mod.id);
      emit(
        _snapshotState(
          await _repository.loadSnapshot(),
          'Uninstalled ${mod.name}.',
        ),
      );
    });
  }

  Future<void> _onDiagnosticBundleRequested(
    DiagnosticBundleRequested event,
    Emitter<LauncherState> emit,
  ) async {
    final install = state.gameInstall;
    if (install == null) {
      return;
    }
    await _guard(emit, 'Created diagnostic bundle.', () async {
      final bundle = await _repository.createDiagnosticBundle(
        install,
        state.resolution,
      );
      emit(
        _snapshotState(
          await _repository.loadSnapshot(),
          'Diagnostic bundle created at ${bundle.path}.',
        ).copyWith(diagnosticBundle: bundle),
      );
    });
  }

  Future<void> _onSelectedProfileExported(
    SelectedProfileExported event,
    Emitter<LauncherState> emit,
  ) async {
    final selected = state.selectedProfile;
    if (selected == null) {
      return;
    }
    await File(event.path).writeAsString(
      const JsonEncoder.withIndent('  ').convert(selected.toJson()),
    );
    emit(state.copyWith(statusMessage: 'Exported profile to ${event.path}.'));
  }

  Future<void> _onProfileImported(
    ProfileImported event,
    Emitter<LauncherState> emit,
  ) async {
    final decoded =
        jsonDecode(await File(event.path).readAsString())
            as Map<String, Object?>;
    final profile = LauncherProfile.fromJson(
      decoded,
    ).copyWith(id: 'profile-${DateTime.now().millisecondsSinceEpoch}');
    final profiles = [...state.profiles, profile];
    await _repository.saveProfiles(profiles, profile.id);
    emit(
      state.copyWith(
        profiles: profiles,
        selectedProfileId: profile.id,
        statusMessage: 'Imported profile ${profile.name}.',
      ),
    );
  }

  LauncherState _snapshotState(
    LauncherSnapshot snapshot,
    String statusMessage, {
    String? selectedModId,
  }) {
    final selected =
        selectedModId ??
        (snapshot.installedMods.any((mod) => mod.id == state.selectedModId)
            ? state.selectedModId
            : null) ??
        (snapshot.installedMods.isEmpty
            ? null
            : snapshot.installedMods.first.id);
    return state.copyWith(
      isBusy: false,
      statusMessage: snapshot.gameInstall == null
          ? 'Select or detect a Robotopia install to begin.'
          : statusMessage,
      gameInstall: snapshot.gameInstall,
      clearGameInstall: snapshot.gameInstall == null,
      profiles: snapshot.profiles,
      selectedProfileId: snapshot.selectedProfileId,
      installedMods: snapshot.installedMods,
      registryMods: snapshot.registryMods,
      packageSources: snapshot.packageSources,
      worldCatalog: snapshot.worldCatalog,
      legacyMods: snapshot.legacyMods,
      recentLog: snapshot.recentLog,
      resolution: _dependencyPlanner.resolveInstalled(snapshot.installedMods),
      launcherUpdates: snapshot.launcherUpdates,
      selectedModId: selected,
      clearSelectedMod: selected == null,
      developerMode: snapshot.developerMode,
      clearError: true,
    );
  }

  Future<void> _guard(
    Emitter<LauncherState> emit,
    String successMessage,
    Future<void> Function() run,
  ) async {
    emit(state.copyWith(isBusy: true, clearError: true));
    try {
      await run();
      if (state.isBusy) {
        emit(
          state.copyWith(
            isBusy: false,
            statusMessage: successMessage,
            clearError: true,
          ),
        );
      }
    } on Object catch (error) {
      emit(
        state.copyWith(
          isBusy: false,
          errorMessage: error.toString(),
          statusMessage: 'Action failed.',
        ),
      );
    }
  }
}
