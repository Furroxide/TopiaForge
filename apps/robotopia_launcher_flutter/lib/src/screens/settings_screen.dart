part of '../screens.dart';

class SettingsScreen extends StatelessWidget {
  const SettingsScreen({super.key, required this.state});

  final LauncherState state;

  @override
  Widget build(BuildContext context) {
    return Column(
      children: [
        const ScreenHeader(
          title: 'Settings',
          subtitle: 'Local launcher paths and runtime management.',
        ),
        Expanded(
          child: SingleChildScrollView(
            padding: const EdgeInsets.fromLTRB(20, 0, 20, 20),
            child: Column(
              children: [
                _GameInstallSettings(state: state),
                const SizedBox(height: 14),
                _PackageSourcesSettings(state: state),
                const SizedBox(height: 14),
                _LauncherDataSettings(state: state),
                const SizedBox(height: 14),
                _LauncherUpdateSettings(state: state),
                const SizedBox(height: 14),
                _DeveloperModeSettings(state: state),
                const SizedBox(height: 14),
                const BorderedPane(
                  child: Text(
                    'RoboPatch behavior is treated as clean-room compatibility. No RoboPatch or Prism Launcher code is copied into this launcher.',
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

class _PackageSourcesSettings extends StatelessWidget {
  const _PackageSourcesSettings({required this.state});

  final LauncherState state;

  @override
  Widget build(BuildContext context) {
    return BorderedPane(
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Row(
            children: [
              Expanded(
                child: Text(
                  'Package Sources',
                  style: Theme.of(context).textTheme.titleMedium,
                ),
              ),
              Wrap(
                spacing: 8,
                children: [
                  OutlinedButton.icon(
                    onPressed: state.isBusy
                        ? null
                        : () => _showAddSourceDialog(context),
                    icon: const Icon(Icons.add_link),
                    label: const Text('Add'),
                  ),
                  OutlinedButton.icon(
                    onPressed: state.isBusy
                        ? null
                        : () => _add(context, const PackageSourcesRefreshed()),
                    icon: const Icon(Icons.refresh),
                    label: const Text('Refresh'),
                  ),
                ],
              ),
            ],
          ),
          const SizedBox(height: 8),
          if (state.packageSources.isEmpty)
            Text(
              'No package sources configured.',
              style: Theme.of(context).textTheme.bodySmall,
            )
          else
            ...state.packageSources.map(
              (source) => ListTile(
                contentPadding: EdgeInsets.zero,
                leading: Switch(
                  value: source.enabled,
                  onChanged: (enabled) => _add(
                    context,
                    PackageSourceEnabledChanged(source.id, enabled),
                  ),
                ),
                title: Text(source.name, overflow: TextOverflow.ellipsis),
                subtitle: Text(source.url, overflow: TextOverflow.ellipsis),
                trailing: IconButton(
                  tooltip: 'Remove source',
                  onPressed: source.builtIn
                      ? null
                      : () => _add(context, PackageSourceRemoved(source.id)),
                  icon: const Icon(Icons.delete),
                ),
              ),
            ),
        ],
      ),
    );
  }
}

class _LauncherUpdateSettings extends StatelessWidget {
  const _LauncherUpdateSettings({required this.state});

  final LauncherState state;

  @override
  Widget build(BuildContext context) {
    final settings = state.launcherUpdates;
    final controller = LauncherUpdateScope.maybeControllerOf(context);
    return BorderedPane(
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Row(
            children: [
              Expanded(
                child: Text(
                  'Launcher Updates',
                  style: Theme.of(context).textTheme.titleMedium,
                ),
              ),
              OutlinedButton.icon(
                onPressed: controller == null || state.isBusy
                    ? null
                    : () => _checkLauncherUpdates(
                        context,
                        controller,
                        settings.channel,
                      ),
                icon: const Icon(Icons.system_update_alt),
                label: const Text('Check'),
              ),
            ],
          ),
          SwitchListTile(
            contentPadding: EdgeInsets.zero,
            value: settings.enabled,
            onChanged: (enabled) =>
                _add(context, LauncherUpdateSettingsChanged(enabled: enabled)),
            title: const Text('Enable launcher updates'),
            subtitle: Text(settings.appArchiveUrl),
          ),
          SwitchListTile(
            contentPadding: EdgeInsets.zero,
            value: settings.checkAutomatically,
            onChanged: !settings.enabled
                ? null
                : (enabled) => _add(
                    context,
                    LauncherUpdateSettingsChanged(checkAutomatically: enabled),
                  ),
            title: const Text('Check automatically on startup'),
          ),
          const SizedBox(height: 8),
          DropdownButtonFormField<LauncherUpdateChannel>(
            initialValue: settings.channel,
            decoration: const InputDecoration(
              labelText: 'Release channel',
              border: OutlineInputBorder(),
            ),
            items: [
              for (final channel in LauncherUpdateChannel.values)
                DropdownMenuItem(
                  value: channel,
                  child: Text(_updateChannelLabel(channel)),
                ),
            ],
            onChanged: !settings.enabled
                ? null
                : (channel) {
                    if (channel != null) {
                      _add(
                        context,
                        LauncherUpdateSettingsChanged(channel: channel),
                      );
                    }
                  },
          ),
        ],
      ),
    );
  }
}

class _DeveloperModeSettings extends StatelessWidget {
  const _DeveloperModeSettings({required this.state});

  final LauncherState state;

  @override
  Widget build(BuildContext context) {
    return BorderedPane(
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Text('Developer', style: Theme.of(context).textTheme.titleMedium),
          SwitchListTile(
            contentPadding: EdgeInsets.zero,
            value: state.developerMode,
            onChanged: (enabled) =>
                _add(context, DeveloperModeToggled(enabled)),
            title: const Text('Developer mode'),
            subtitle: const Text(
              'Show the Developer tab (build mods, UGC live-sync). Off by default — '
              'installing and playing mods needs no developer tools.',
            ),
          ),
        ],
      ),
    );
  }
}

class _GameInstallSettings extends StatelessWidget {
  const _GameInstallSettings({required this.state});

  final LauncherState state;

  @override
  Widget build(BuildContext context) {
    return BorderedPane(
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Text('Game Install', style: Theme.of(context).textTheme.titleMedium),
          const SizedBox(height: 8),
          _keyValue('Path', state.gameInstall?.path ?? 'Not selected'),
          _keyValue(
            'Executable',
            state.gameInstall?.executablePath ?? 'Not selected',
          ),
          const SizedBox(height: 12),
          Wrap(
            spacing: 8,
            runSpacing: 8,
            children: [
              OutlinedButton.icon(
                onPressed: () => _add(context, const KnownInstallDetected()),
                icon: const Icon(Icons.search),
                label: const Text('Detect'),
              ),
              OutlinedButton.icon(
                onPressed: () => _chooseGameFolder(context),
                icon: const Icon(Icons.folder_open),
                label: const Text('Select Folder'),
              ),
              OutlinedButton.icon(
                onPressed: state.gameInstall == null
                    ? null
                    : () => _add(context, const GameFolderOpened()),
                icon: const Icon(Icons.folder),
                label: const Text('Open Game Folder'),
              ),
              OutlinedButton.icon(
                onPressed: state.gameInstall == null
                    ? null
                    : () => _add(context, const RuntimeRepaired()),
                icon: const Icon(Icons.build),
                label: const Text('Repair Runtime'),
              ),
            ],
          ),
        ],
      ),
    );
  }
}

class _LauncherDataSettings extends StatelessWidget {
  const _LauncherDataSettings({required this.state});

  final LauncherState state;

  @override
  Widget build(BuildContext context) {
    return BorderedPane(
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Text('Launcher Data', style: Theme.of(context).textTheme.titleMedium),
          const SizedBox(height: 8),
          _keyValue('Data root', context.read<LauncherBloc>().dataRoot),
          const SizedBox(height: 12),
          OutlinedButton.icon(
            onPressed: () => _add(context, const DataFolderOpened()),
            icon: const Icon(Icons.folder_open),
            label: const Text('Open Data Folder'),
          ),
        ],
      ),
    );
  }
}
