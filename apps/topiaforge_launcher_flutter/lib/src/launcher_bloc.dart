import 'dart:async';
import 'dart:convert';

import 'package:bloc_concurrency/bloc_concurrency.dart';
import 'package:flutter_bloc/flutter_bloc.dart';
import 'package:launcher_domain/launcher_domain.dart';

import 'launcher_event.dart';
import 'launcher_section.dart';
import 'launcher_state.dart';

part 'launcher_bloc_actions.dart';
part 'launcher_event_dispatch.dart';
part 'launcher_game_install_actions.dart';
part 'launcher_profile_actions.dart';
part 'launcher_developer_ugc_actions.dart';
part 'launcher_developer_project_actions.dart';
part 'launcher_developer_actions.dart';

class LauncherBloc extends Bloc<LauncherEvent, LauncherState> {
  LauncherBloc(this._repository, {DeveloperRepository? developerRepository})
    : _developerRepository = developerRepository,
      super(LauncherState.initial()) {
    on<LauncherEvent>(_dispatchEvent, transformer: sequential());
    _ugcPublisherSub = _repository.ugcPublisherEvents.listen((event) {
      if (isClosed) {
        return;
      }
      switch (event) {
        case UgcPublisherOutput(:final sessionId, :final line):
          add(DeveloperUgcSidecarOutput(line, sessionId));
        case UgcPublisherExited(:final sessionId, :final exitCode):
          add(DeveloperUgcPublisherExited(sessionId, exitCode));
      }
    });
  }

  final LauncherRepository _repository;
  final DeveloperRepository? _developerRepository;
  final DependencyPlanner _dependencyPlanner = const DependencyPlanner();

  StreamSubscription<UgcPublisherEvent>? _ugcPublisherSub;

  // True while a "Go Live" is waiting for the publisher to report its live document URL before launching the game
  // (so the game auto-connects to the real document, not an empty one).
  bool _ugcGoLivePending = false;
  int? _ugcPublisherSessionId;
  Completer<void>? _ugcMutationLock;

  String get dataRoot => _repository.dataRoot;

  Future<T> _withUgcMutation<T>(Future<T> Function() run) async {
    while (_ugcMutationLock != null) {
      final pending = _ugcMutationLock!;
      await pending.future;
    }
    final lock = Completer<void>();
    _ugcMutationLock = lock;
    try {
      return await run();
    } finally {
      _ugcMutationLock = null;
      lock.complete();
    }
  }

  @override
  Future<void> close() {
    final publisherSubscription = _ugcPublisherSub;
    _ugcPublisherSub = null;
    // Initiate both stream shutdowns synchronously so no new publisher output
    // or UI events can enter while pending handlers finish. Repository
    // disposal then owns sidecar shutdown instead of racing a handler with a
    // duplicate stop request.
    final publisherClose = publisherSubscription?.cancel();
    final blocClose = super.close();
    return Future.wait<void>([
      ?publisherClose,
      blocClose,
    ]).whenComplete(_repository.dispose);
  }

  Future<void> _onLoad(LauncherEvent event, Emitter<LauncherState> emit) async {
    await _guard(emit, 'Refreshed launcher state.', () async {
      final snapshot = await _repository.loadSnapshot();
      emit(_snapshotState(snapshot, 'Ready.'));
    });
  }

  Future<void> _onSectionSelected(
    LauncherSectionSelected event,
    Emitter<LauncherState> emit,
  ) async {
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
      // Preserve the serialized contract without allowing a stale setting or
      // synthetic event to reactivate an updater that cannot yet verify
      // owner-signed metadata and fully bound extraction.
      enabled: false,
      checkAutomatically: event.checkAutomatically,
      channel: event.channel,
    );
    await _repository.saveLauncherUpdateSettings(settings);
    emit(
      state.copyWith(
        launcherUpdates: settings,
        statusMessage: 'Launcher updates require a manual package download.',
      ),
    );
  }

  Future<void> _onModSelected(
    ModSelected event,
    Emitter<LauncherState> emit,
  ) async {
    emit(state.copyWith(selectedModId: event.modId));
  }

  Future<void> _onModSearchChanged(
    ModSearchChanged event,
    Emitter<LauncherState> emit,
  ) async {
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
    await _repository.exportProfile(selected, event.path);
    emit(state.copyWith(statusMessage: 'Exported profile to ${event.path}.'));
  }

  Future<void> _onProfileImported(
    ProfileImported event,
    Emitter<LauncherState> emit,
  ) async {
    final imported = await _repository.importProfile(event.path);
    final profile = imported.copyWith(
      id: 'profile-${DateTime.now().millisecondsSinceEpoch}',
    );
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
      sourceStatuses: snapshot.sourceStatuses,
      worldCatalog: snapshot.worldCatalog,
      recentLog: snapshot.recentLog,
      launcherLog: snapshot.launcherLog,
      resolution: _dependencyPlanner.resolveInstalled(
        snapshot.installedMods,
        gameVersion: snapshot.gameInstall?.gameVersion,
        requireKnownGameVersion: true,
      ),
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
