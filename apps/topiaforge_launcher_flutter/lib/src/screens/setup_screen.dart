part of '../screens.dart';

class SetupScreen extends StatelessWidget {
  const SetupScreen({super.key, required this.state});

  final LauncherState state;

  @override
  Widget build(BuildContext context) {
    final install = state.gameInstall;
    if (install == null) {
      return _SetupFirstRunPanel(state: state);
    }

    final profile = state.selectedProfile;
    return Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        ScreenHeader(title: 'Setup', subtitle: install.path),
        Expanded(
          child: SingleChildScrollView(
            padding: const EdgeInsets.fromLTRB(20, 0, 20, 20),
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Wrap(
                  spacing: 8,
                  runSpacing: 8,
                  children: [
                    _componentPill('BepInEx', install.bepInExStatus),
                    _componentPill('Loader', install.loaderStatus),
                    StatusPill(
                      label: '${state.installedMods.length} mods installed',
                      tone: StatusTone.info,
                      icon: Icons.extension,
                    ),
                    StatusPill(
                      label: profile?.launchSettings.safeMode == true
                          ? 'Safe mode'
                          : 'Normal launch',
                      tone: profile?.launchSettings.safeMode == true
                          ? StatusTone.warning
                          : StatusTone.good,
                      icon: profile?.launchSettings.safeMode == true
                          ? Icons.health_and_safety
                          : Icons.rocket_launch,
                    ),
                  ],
                ),
                const SizedBox(height: 16),
                if (_shouldShowGameInstallSelector(state)) ...[
                  _GameInstallCandidateSelector(state: state),
                  const SizedBox(height: 14),
                ],
                BorderedPane(
                  child: Column(
                    crossAxisAlignment: CrossAxisAlignment.start,
                    children: [
                      Text(
                        'Runtime',
                        style: Theme.of(context).textTheme.titleMedium,
                      ),
                      const SizedBox(height: 8),
                      _keyValue('Executable', install.executablePath),
                      _keyValue('BepInEx', install.bepInExStatus.name),
                      _keyValue('TopiaForge loader', install.loaderStatus.name),
                      if (install.issues.isNotEmpty) ...[
                        const SizedBox(height: 10),
                        ...install.issues.map(_issueTile),
                      ],
                      const SizedBox(height: 12),
                      Wrap(
                        spacing: 8,
                        runSpacing: 8,
                        children: [
                          OutlinedButton.icon(
                            onPressed: state.isBusy
                                ? null
                                : () => _add(context, const RuntimeRepaired()),
                            icon: const Icon(Icons.build),
                            label: const Text('Repair Runtime'),
                          ),
                          OutlinedButton.icon(
                            onPressed: () =>
                                _add(context, const GameFolderOpened()),
                            icon: const Icon(Icons.folder_open),
                            label: const Text('Open Game Folder'),
                          ),
                          OutlinedButton.icon(
                            onPressed: () => _add(
                              context,
                              const LauncherSectionSelected(
                                LauncherSection.diagnostics,
                              ),
                            ),
                            icon: const Icon(Icons.monitor_heart),
                            label: const Text('Diagnostics'),
                          ),
                        ],
                      ),
                    ],
                  ),
                ),
                const SizedBox(height: 14),
                const BorderedPane(
                  child: Text(
                    'C# mods are trusted code and execute inside the Robotopia process. Install packages only from sources you trust.',
                  ),
                ),
                const SizedBox(height: 14),
                _WorldLaunchSettings(state: state),
                const SizedBox(height: 14),
                BorderedPane(
                  child: Column(
                    crossAxisAlignment: CrossAxisAlignment.start,
                    children: [
                      Text(
                        'Load Order',
                        style: Theme.of(context).textTheme.titleMedium,
                      ),
                      const SizedBox(height: 8),
                      // The resolver owns this order: it is derived from dependencies, and a mod
                      // moved by hand would just be moved back. The drag handle that used to lead
                      // each row promised a reorder that has never existed anywhere in the product.
                      Text(
                        'Set by dependencies between mods, not editable.',
                        style: Theme.of(context).textTheme.bodySmall,
                      ),
                      const SizedBox(height: 8),
                      if (state.resolution.orderedMods.isEmpty)
                        Text(
                          'No enabled mods in the current load order.',
                          style: Theme.of(context).textTheme.bodySmall,
                        )
                      else
                        ...state.resolution.orderedMods.indexed.map(
                          (entry) => ListTile(
                            leading: Text(
                              '${entry.$1 + 1}',
                              style: Theme.of(context).textTheme.bodySmall,
                            ),
                            title: Text(
                              entry.$2.name,
                              overflow: TextOverflow.ellipsis,
                            ),
                            subtitle: Text(
                              '${entry.$2.id} ${entry.$2.version}',
                            ),
                            trailing: entry.$2.restartRequired
                                ? _restartPill(context, state)
                                : null,
                          ),
                        ),
                    ],
                  ),
                ),
              ],
            ),
          ),
        ),
      ],
    );
  }
}

class _WorldLaunchSettings extends StatelessWidget {
  const _WorldLaunchSettings({required this.state});

  final LauncherState state;

  @override
  Widget build(BuildContext context) {
    return BorderedPane(child: _WorldLaunchControls(state: state));
  }
}

class _WorldLaunchControls extends StatelessWidget {
  const _WorldLaunchControls({required this.state});

  final LauncherState state;

  static String _loadModeLabel(String mode) {
    switch (mode) {
      case WorldSelection.sceneReplacement:
        return 'Scene replacement';
      case WorldSelection.additiveArena:
      default:
        return 'Additive arena';
    }
  }

  @override
  Widget build(BuildContext context) {
    final selection =
        state.selectedProfile?.worldSelection ?? const WorldSelection();
    final worlds = state.worldCatalog.worlds;
    final gamemodes = state.worldCatalog.gamemodes;
    final worldId = worlds.any((world) => world.id == selection.worldId)
        ? selection.worldId
        : worlds.first.id;
    final gamemodeId = gamemodes.any((mode) => mode.id == selection.gamemodeId)
        ? selection.gamemodeId
        : gamemodes.first.id;

    // Only offer the load modes the selected world can actually honour. A checkpoint/first-party level loads
    // via the game loader and the open sandbox is additive-only, so for those the control is locked to the one
    // valid mode instead of presenting a choice the runtime would silently override. Clamping the value also
    // prevents a stale/unknown persisted loadMode from asserting the dropdown.
    final selectedWorld = worlds.firstWhere(
      (world) => world.id == worldId,
      orElse: () => worlds.first,
    );
    final supportedModes = selectedWorld.supportedLoadModes;
    final loadModeOptions = [
      for (final mode in WorldSelection.supportedLoadModes)
        if (supportedModes.isEmpty || supportedModes.contains(mode)) mode,
    ];
    final effectiveLoadMode = loadModeOptions.contains(selection.loadMode)
        ? selection.loadMode
        : loadModeOptions.first;
    final loadModeEnabled = loadModeOptions.length > 1;

    return Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        Text('World', style: Theme.of(context).textTheme.titleMedium),
        const SizedBox(height: 10),
        Row(
          children: [
            Expanded(
              child: DropdownButtonFormField<String>(
                initialValue: worldId,
                isExpanded: true,
                decoration: const InputDecoration(labelText: 'World'),
                items: [
                  for (final world in worlds)
                    DropdownMenuItem(
                      value: world.id,
                      child: Text(world.name, overflow: TextOverflow.ellipsis),
                    ),
                ],
                onChanged: (value) => value == null
                    ? null
                    : _add(context, WorldSelectionChanged(worldId: value)),
              ),
            ),
            const SizedBox(width: 10),
            Expanded(
              child: DropdownButtonFormField<String>(
                initialValue: gamemodeId,
                isExpanded: true,
                decoration: const InputDecoration(labelText: 'Gamemode'),
                items: [
                  for (final mode in gamemodes)
                    DropdownMenuItem(
                      value: mode.id,
                      child: Text(mode.name, overflow: TextOverflow.ellipsis),
                    ),
                ],
                onChanged: (value) => value == null
                    ? null
                    : _add(context, WorldSelectionChanged(gamemodeId: value)),
              ),
            ),
          ],
        ),
        const SizedBox(height: 10),
        Row(
          children: [
            Expanded(
              child: DropdownButtonFormField<String>(
                initialValue: effectiveLoadMode,
                isExpanded: true,
                decoration: InputDecoration(
                  labelText: 'Load mode',
                  helperText: loadModeEnabled
                      ? null
                      : 'Determined by the selected world',
                ),
                items: [
                  for (final mode in loadModeOptions)
                    DropdownMenuItem(
                      value: mode,
                      child: Text(_WorldLaunchControls._loadModeLabel(mode)),
                    ),
                ],
                onChanged: loadModeEnabled
                    ? (value) => value == null
                          ? null
                          : _add(
                              context,
                              WorldSelectionChanged(loadMode: value),
                            )
                    : null,
              ),
            ),
          ],
        ),
      ],
    );
  }
}

class _SetupFirstRunPanel extends StatelessWidget {
  const _SetupFirstRunPanel({required this.state});

  final LauncherState state;

  @override
  Widget build(BuildContext context) {
    return Column(
      children: [
        const ScreenHeader(
          title: 'Setup',
          subtitle: 'Set up TopiaForge, BepInEx, and the runtime loader.',
        ),
        Expanded(
          child: EmptyStatePanel(
            icon: Icons.folder_open,
            title: 'Select TopiaForge',
            message:
                'The launcher validates Robotopia.exe and then offers one-click runtime setup.',
            brandAsset: TopiaForgeBrandAssets.babyStitch,
            action: Wrap(
              spacing: 8,
              runSpacing: 8,
              alignment: WrapAlignment.center,
              children: [
                FilledButton.icon(
                  onPressed: state.isBusy
                      ? null
                      : () => _add(context, const KnownInstallDetected()),
                  icon: const Icon(Icons.search),
                  label: const Text('Detect Install'),
                ),
                OutlinedButton.icon(
                  onPressed: state.isBusy
                      ? null
                      : () => _chooseGameFolder(context),
                  icon: const Icon(Icons.folder_open),
                  label: const Text('Select Folder'),
                ),
              ],
            ),
          ),
        ),
      ],
    );
  }
}
