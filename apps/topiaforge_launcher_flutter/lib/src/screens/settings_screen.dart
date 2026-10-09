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
                if (_shouldShowGameInstallSelector(state)) ...[
                  const SizedBox(height: 14),
                  _GameInstallCandidateSelector(state: state),
                ],
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
            ...state.packageSources.map((source) {
              final status = _sourceStatus(state, source.id);
              return ListTile(
                contentPadding: EdgeInsets.zero,
                leading: Switch(
                  value: source.enabled,
                  onChanged: (enabled) => _add(
                    context,
                    PackageSourceEnabledChanged(source.id, enabled),
                  ),
                ),
                title: Text(source.name, overflow: TextOverflow.ellipsis),
                subtitle: Column(
                  crossAxisAlignment: CrossAxisAlignment.start,
                  children: [
                    Text(source.url, overflow: TextOverflow.ellipsis),
                    if (source.enabled && status != null)
                      Text(
                        status.message,
                        maxLines: 2,
                        overflow: TextOverflow.ellipsis,
                        style: Theme.of(context).textTheme.bodySmall?.copyWith(
                          color: status.ok
                              ? TopiaForgePalette.good
                              : Theme.of(context).colorScheme.error,
                        ),
                      ),
                  ],
                ),
                trailing: IconButton(
                  tooltip: 'Remove source',
                  onPressed: source.builtIn
                      ? null
                      : () => _add(context, PackageSourceRemoved(source.id)),
                  icon: const Icon(Icons.delete),
                ),
              );
            }),
        ],
      ),
    );
  }
}

PackageSourceStatus? _sourceStatus(LauncherState state, String sourceId) {
  for (final status in state.sourceStatuses) {
    if (status.sourceId == sourceId) {
      return status;
    }
  }
  return null;
}

class _LauncherUpdateSettings extends StatelessWidget {
  const _LauncherUpdateSettings({required this.state});

  final LauncherState state;

  @override
  Widget build(BuildContext context) {
    final settings = state.launcherUpdates;
    final status = state.launcherUpdateStatus;
    final checking =
        status.phase == LauncherUpdatePhase.checking ||
        status.phase == LauncherUpdatePhase.downloading;
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
              StatusPill(
                label: _phaseLabel(status.phase),
                tone: _phaseTone(status.phase),
              ),
            ],
          ),
          const SizedBox(height: 8),
          Text(
            'TopiaForge ${TopiaForgeLauncherBuild.version} verifies '
            'Ed25519-signed release metadata before downloading a complete '
            'platform package. Installing always requires confirmation.',
          ),
          const SizedBox(height: 8),
          SwitchListTile(
            contentPadding: EdgeInsets.zero,
            value: settings.enabled,
            onChanged: (enabled) =>
                _add(context, LauncherUpdateSettingsChanged(enabled: enabled)),
            title: const Text('Enable signed update checks'),
            subtitle: const Text(
              'Prerelease builds check the beta channel by default.',
            ),
          ),
          SwitchListTile(
            contentPadding: EdgeInsets.zero,
            value: settings.checkAutomatically,
            onChanged: settings.enabled
                ? (enabled) => _add(
                    context,
                    LauncherUpdateSettingsChanged(checkAutomatically: enabled),
                  )
                : null,
            title: const Text('Check at startup'),
            subtitle: const Text('A persisted cooldown limits network checks.'),
          ),
          const SizedBox(height: 8),
          DropdownButtonFormField<LauncherUpdateChannel>(
            initialValue: settings.channel,
            decoration: const InputDecoration(labelText: 'Update channel'),
            items: const [
              DropdownMenuItem(
                value: LauncherUpdateChannel.release,
                child: Text('Stable'),
              ),
              DropdownMenuItem(
                value: LauncherUpdateChannel.beta,
                child: Text('Beta (includes stable)'),
              ),
            ],
            onChanged: settings.enabled
                ? (channel) {
                    if (channel != null) {
                      _add(
                        context,
                        LauncherUpdateSettingsChanged(channel: channel),
                      );
                    }
                  }
                : null,
          ),
          const SizedBox(height: 12),
          if (checking) ...[
            LinearProgressIndicator(
              value: status.phase == LauncherUpdatePhase.downloading
                  ? status.progress.clamp(0, 1)
                  : null,
            ),
            const SizedBox(height: 8),
          ],
          if (status.message.isNotEmpty)
            SelectableText(status.message)
          else
            const Text('No update check has run in this session.'),
          if (status.candidate case final candidate?) ...[
            const SizedBox(height: 8),
            SelectableText(
              'Candidate ${candidate.version} • ${candidate.signingKeyId}\n'
              'Payload SHA-256 ${candidate.payloadSha256}',
            ),
          ],
          const SizedBox(height: 12),
          Wrap(
            spacing: 8,
            runSpacing: 8,
            children: [
              OutlinedButton.icon(
                onPressed: settings.enabled && !checking
                    ? () => _add(context, const LauncherUpdateCheckRequested())
                    : null,
                icon: const Icon(Icons.refresh),
                label: const Text('Check now'),
              ),
              if (status.phase == LauncherUpdatePhase.available)
                FilledButton.icon(
                  onPressed: () =>
                      _add(context, const LauncherUpdateDownloadRequested()),
                  icon: const Icon(Icons.download),
                  label: const Text('Download and verify'),
                ),
              if (status.phase == LauncherUpdatePhase.staged)
                FilledButton.icon(
                  onPressed: () => _confirmInstall(context),
                  icon: const Icon(Icons.restart_alt),
                  label: const Text('Restart and install'),
                ),
            ],
          ),
          const SizedBox(height: 8),
          const SelectableText(
            'Recovery and manual downloads: '
            'https://github.com/furroxide/TopiaForge/releases',
          ),
        ],
      ),
    );
  }

  Future<void> _confirmInstall(BuildContext context) async {
    final confirmed = await showDialog<bool>(
      context: context,
      builder: (dialogContext) => AlertDialog(
        title: const Text('Install verified update?'),
        content: const Text(
          'TopiaForge will close, replace the complete portable package, and '
          'roll back automatically if the updated launcher does not report a '
          'healthy startup.',
        ),
        actions: [
          TextButton(
            onPressed: () => Navigator.pop(dialogContext, false),
            child: const Text('Cancel'),
          ),
          FilledButton(
            onPressed: () => Navigator.pop(dialogContext, true),
            child: const Text('Restart and install'),
          ),
        ],
      ),
    );
    if (confirmed == true && context.mounted) {
      _add(context, const LauncherUpdateInstallConfirmed());
    }
  }
}

String _phaseLabel(LauncherUpdatePhase phase) => switch (phase) {
  LauncherUpdatePhase.idle => 'Idle',
  LauncherUpdatePhase.checking => 'Checking',
  LauncherUpdatePhase.current => 'Current',
  LauncherUpdatePhase.available => 'Available',
  LauncherUpdatePhase.downloading => 'Downloading',
  LauncherUpdatePhase.staged => 'Verified',
  LauncherUpdatePhase.applying => 'Restarting',
  LauncherUpdatePhase.recovering => 'Recovering',
  LauncherUpdatePhase.failed => 'Attention',
};

StatusTone _phaseTone(LauncherUpdatePhase phase) => switch (phase) {
  LauncherUpdatePhase.failed => StatusTone.danger,
  LauncherUpdatePhase.available ||
  LauncherUpdatePhase.downloading ||
  LauncherUpdatePhase.staged => StatusTone.warning,
  LauncherUpdatePhase.current => StatusTone.good,
  _ => StatusTone.neutral,
};

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
              'Show the Developer tab (build mods). Off by default — '
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
