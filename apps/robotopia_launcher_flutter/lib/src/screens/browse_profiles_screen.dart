part of '../screens.dart';

class BrowseScreen extends StatelessWidget {
  const BrowseScreen({super.key, required this.state});

  final LauncherState state;

  @override
  Widget build(BuildContext context) {
    return Column(
      children: [
        const ScreenHeader(
          title: 'Browse',
          subtitle:
              'Local/static registry entries behind the repository interface.',
        ),
        Expanded(
          child: Padding(
            padding: const EdgeInsets.fromLTRB(20, 0, 20, 20),
            child: BorderedPane(
              padding: EdgeInsets.zero,
              child: state.registryMods.isEmpty
                  ? const EmptyStatePanel(
                      icon: Icons.travel_explore,
                      title: 'No local packages',
                      message:
                          'Build mod packages into dist/ (robotopia pack --all) to list them here.',
                      brandAsset: QuantumWorksBrandAssets.robot,
                    )
                  : ListView.separated(
                      itemCount: state.registryMods.length,
                      separatorBuilder: (context, index) => const Divider(),
                      itemBuilder: (context, index) {
                        final mod = state.registryMods[index];
                        return ListTile(
                          leading: const Icon(Icons.public),
                          title: Text(
                            mod.manifest.name,
                            overflow: TextOverflow.ellipsis,
                          ),
                          subtitle: Text(
                            [
                              mod.manifest.version,
                              if (mod.manifest.category.isNotEmpty)
                                mod.manifest.category,
                              if (mod.sourceName.isNotEmpty) mod.sourceName,
                              if (mod.manifest.dependencies.isNotEmpty)
                                '${mod.manifest.dependencies.length} deps',
                              if (mod.manifest.conflicts.isNotEmpty)
                                '${mod.manifest.conflicts.length} conflicts',
                              if (mod.isInstalled)
                                'installed ${mod.installedVersion}',
                            ].join('  '),
                            overflow: TextOverflow.ellipsis,
                          ),
                          trailing: _RegistryPreviewButton(
                            state: state,
                            mod: mod,
                          ),
                        );
                      },
                    ),
            ),
          ),
        ),
      ],
    );
  }
}

class _RegistryPreviewButton extends StatelessWidget {
  const _RegistryPreviewButton({required this.state, required this.mod});

  final LauncherState state;
  final RegistryMod mod;

  @override
  Widget build(BuildContext context) {
    return Wrap(
      spacing: 8,
      children: [
        if (mod.updateAvailable)
          StatusPill(
            label: 'Update',
            tone: StatusTone.warning,
            icon: Icons.system_update_alt,
            tooltip: _updateTooltip(mod),
          )
        else if (mod.isInstalled)
          const StatusPill(
            label: 'Installed',
            tone: StatusTone.good,
            icon: Icons.check_circle,
            tooltip: 'This registry package is already installed.',
          ),
        OutlinedButton.icon(
          onPressed: _canPreviewRegistryPackage(state, mod)
              ? () => _previewRegistryPackage(context, mod, switchToMods: true)
              : null,
          icon: Icon(
            mod.updateAvailable ? Icons.system_update_alt : Icons.download,
          ),
          label: Text(mod.updateAvailable ? 'Preview Update' : 'Preview'),
        ),
      ],
    );
  }
}

class ProfilesScreen extends StatelessWidget {
  const ProfilesScreen({super.key, required this.state});

  final LauncherState state;

  @override
  Widget build(BuildContext context) {
    final selected = state.selectedProfile;
    return Column(
      children: [
        ScreenHeader(
          title: 'Profiles',
          subtitle:
              'Per-profile mod selections, versions, launch settings, and import/export.',
          trailing: Wrap(
            spacing: 8,
            children: [
              OutlinedButton.icon(
                onPressed: () => _add(context, const ProfileCreated()),
                icon: const Icon(Icons.add),
                label: const Text('Create'),
              ),
              OutlinedButton.icon(
                onPressed: selected == null
                    ? null
                    : () => _add(context, const SelectedProfileDuplicated()),
                icon: const Icon(Icons.copy),
                label: const Text('Duplicate'),
              ),
              OutlinedButton.icon(
                onPressed: selected == null
                    ? null
                    : () => _exportProfile(context),
                icon: const Icon(Icons.upload_file),
                label: const Text('Export'),
              ),
              OutlinedButton.icon(
                onPressed: () => _importProfile(context),
                icon: const Icon(Icons.download_for_offline),
                label: const Text('Import'),
              ),
            ],
          ),
        ),
        Expanded(
          child: Padding(
            padding: const EdgeInsets.fromLTRB(20, 0, 20, 20),
            child: Row(
              children: [
                Expanded(child: _ProfilesList(state: state)),
                const SizedBox(width: 12),
                Expanded(child: _ProfileDetail(state: state)),
              ],
            ),
          ),
        ),
      ],
    );
  }
}

class _ProfilesList extends StatelessWidget {
  const _ProfilesList({required this.state});

  final LauncherState state;

  @override
  Widget build(BuildContext context) {
    return BorderedPane(
      padding: EdgeInsets.zero,
      child: ListView.separated(
        itemCount: state.profiles.length,
        separatorBuilder: (context, index) => const Divider(),
        itemBuilder: (context, index) {
          final profile = state.profiles[index];
          return ListTile(
            selected: profile.id == state.selectedProfileId,
            leading: Icon(
              profile.launchSettings.safeMode
                  ? Icons.health_and_safety
                  : Icons.layers,
            ),
            title: Text(profile.name, overflow: TextOverflow.ellipsis),
            subtitle: Text(profile.id, overflow: TextOverflow.ellipsis),
            onTap: () => _add(context, ProfileSelected(profile.id)),
          );
        },
      ),
    );
  }
}

class _ProfileDetail extends StatelessWidget {
  const _ProfileDetail({required this.state});

  final LauncherState state;

  @override
  Widget build(BuildContext context) {
    final selected = state.selectedProfile;
    return BorderedPane(
      child: selected == null
          ? const SizedBox.shrink()
          : ListView(
              padding: EdgeInsets.zero,
              children: [
                Text(
                  selected.name,
                  style: Theme.of(context).textTheme.titleMedium,
                ),
                const SizedBox(height: 10),
                SwitchListTile(
                  contentPadding: EdgeInsets.zero,
                  title: const Text('Safe mode'),
                  subtitle: const Text('Disables all mods before launch.'),
                  value: selected.launchSettings.safeMode,
                  onChanged: (enabled) =>
                      _add(context, SafeModeToggled(enabled)),
                ),
                _keyValue(
                  'Enabled mods',
                  selected.enabledMods.isEmpty
                      ? 'Uses manager state'
                      : selected.enabledMods.length.toString(),
                ),
                _keyValue(
                  'Selected versions',
                  selected.selectedVersions.length.toString(),
                ),
                const SizedBox(height: 10),
                _WorldLaunchControls(state: state),
                const SizedBox(height: 16),
                OutlinedButton.icon(
                  onPressed: state.profiles.length <= 1
                      ? null
                      : () => _confirm(
                          context,
                          title: 'Delete profile?',
                          message: selected.name,
                          action: () =>
                              _add(context, const SelectedProfileDeleted()),
                        ),
                  icon: const Icon(Icons.delete),
                  label: const Text('Delete Profile'),
                ),
              ],
            ),
    );
  }
}
