part of '../screens.dart';

class ModsScreen extends StatelessWidget {
  const ModsScreen({super.key, required this.state});

  final LauncherState state;

  @override
  Widget build(BuildContext context) {
    if (state.gameInstall == null) {
      return _SetupFirstRunPanel(state: state);
    }

    final selected = state.selectedMod;
    return Column(
      children: [
        ScreenHeader(
          title: 'Mods',
          subtitle:
              'Installed packages, dependency health, updates, and local imports.',
          trailing: Wrap(
            spacing: 8,
            children: [
              OutlinedButton.icon(
                onPressed: state.isBusy ? null : () => _choosePackage(context),
                icon: const Icon(Icons.archive),
                label: const Text('Install Local'),
              ),
              OutlinedButton.icon(
                onPressed: state.isBusy
                    ? null
                    : () => _add(context, const InboxPackagesInstalled()),
                icon: const Icon(Icons.move_to_inbox),
                label: const Text('Install Inbox'),
              ),
              OutlinedButton.icon(
                onPressed: state.installedMods.isEmpty
                    ? null
                    : () => _confirm(
                        context,
                        title: 'Disable all mods?',
                        message:
                            'TopiaForge will need a restart before loaded C# assemblies change.',
                        action: () => _add(context, const AllModsDisabled()),
                      ),
                icon: const Icon(Icons.toggle_off),
                label: const Text('Disable All'),
              ),
            ],
          ),
        ),
        Padding(
          padding: const EdgeInsets.fromLTRB(20, 0, 20, 12),
          child: TextField(
            decoration: const InputDecoration(
              prefixIcon: Icon(Icons.search),
              labelText: 'Search installed mods',
            ),
            onChanged: (value) => _add(context, ModSearchChanged(value)),
          ),
        ),
        if (state.installPlan != null)
          Flexible(
            flex: 2,
            child: Padding(
              padding: const EdgeInsets.fromLTRB(20, 0, 20, 12),
              child: SingleChildScrollView(
                primary: false,
                child: _InstallPlanPane(state: state),
              ),
            ),
          ),
        Expanded(
          flex: state.installPlan == null ? 1 : 3,
          child: Padding(
            padding: const EdgeInsets.fromLTRB(20, 0, 20, 20),
            child: Row(
              children: [
                Expanded(
                  flex: 3,
                  child: _ModList(state: state, selected: selected),
                ),
                const SizedBox(width: 12),
                Expanded(
                  flex: 2,
                  child: BorderedPane(
                    child: selected == null
                        ? const EmptyStatePanel(
                            icon: Icons.info_outline,
                            title: 'Select a mod',
                            message:
                                'Details, conflicts, and actions appear here.',
                            brandAsset: TopiaForgeBrandAssets.sheriff,
                          )
                        : _ModDetail(state: state, mod: selected),
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

class _ModList extends StatelessWidget {
  const _ModList({required this.state, required this.selected});

  final LauncherState state;
  final InstalledMod? selected;

  @override
  Widget build(BuildContext context) {
    return BorderedPane(
      padding: EdgeInsets.zero,
      clipBehavior: Clip.antiAlias,
      child: state.filteredMods.isEmpty
          ? const EmptyStatePanel(
              icon: Icons.extension_off,
              title: 'No installed mods',
              message:
                  'Install a .topiaforgemod package or process the package inbox.',
              brandAsset: TopiaForgeBrandAssets.robot,
            )
          : ListView.separated(
              itemCount: state.filteredMods.length,
              separatorBuilder: (context, index) => const Divider(),
              itemBuilder: (context, index) {
                final mod = state.filteredMods[index];
                final issues = _issuesForMod(state, mod.id);
                final update = _availableUpdateForMod(state, mod);
                return Material(
                  color: selected?.id == mod.id
                      ? TopiaForgePalette.surfaceAlt
                      : Colors.transparent,
                  child: ListTile(
                    selected: selected?.id == mod.id,
                    onTap: () => _add(context, ModSelected(mod.id)),
                    leading: Icon(
                      mod.enabled ? Icons.check_circle : Icons.pause_circle,
                      color: mod.enabled
                          ? TopiaForgePalette.launch
                          : TopiaForgePalette.mutedText,
                    ),
                    title: Text(mod.name, overflow: TextOverflow.ellipsis),
                    subtitle: Text(
                      '${mod.id}  ${mod.version}',
                      overflow: TextOverflow.ellipsis,
                    ),
                    trailing: Wrap(
                      spacing: 6,
                      children: [
                        if (update != null)
                          StatusPill(
                            label: 'Update',
                            tone: StatusTone.warning,
                            icon: Icons.system_update_alt,
                            tooltip: _updateTooltip(update),
                          ),
                        if (mod.restartRequired) _restartPill(context, state),
                        if (issues.isNotEmpty || mod.errors.isNotEmpty)
                          const StatusPill(
                            label: 'Issue',
                            tone: StatusTone.danger,
                            icon: Icons.error,
                            tooltip:
                                'This mod has dependency, conflict, or manifest issues. Select it for details.',
                          ),
                      ],
                    ),
                  ),
                );
              },
            ),
    );
  }
}

class _InstallPlanPane extends StatelessWidget {
  const _InstallPlanPane({required this.state});

  final LauncherState state;

  @override
  Widget build(BuildContext context) {
    final plan = state.installPlan!;
    return BorderedPane(
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Row(
            children: [
              Expanded(
                child: Text(
                  '${plan.manifest.name} ${plan.manifest.version}',
                  style: Theme.of(context).textTheme.titleMedium,
                  overflow: TextOverflow.ellipsis,
                ),
              ),
              StatusPill(
                label: plan.hasBlockingIssues ? 'Blocked' : 'Ready',
                tone: plan.hasBlockingIssues
                    ? StatusTone.danger
                    : StatusTone.good,
              ),
            ],
          ),
          const SizedBox(height: 8),
          _keyValue('Package SHA-256', plan.packageSha256),
          if (plan.dependenciesToInstall.isNotEmpty)
            _keyValue(
              'Missing dependencies',
              plan.dependenciesToInstall
                  .map((dependency) => dependency.id)
                  .join(', '),
            ),
          if (plan.optionalDependenciesMissing.isNotEmpty)
            _keyValue(
              'Optional missing',
              plan.optionalDependenciesMissing
                  .map((dependency) => dependency.id)
                  .join(', '),
            ),
          _keyValue(
            'Declared capabilities',
            plan.requiredPermissions.isEmpty
                ? 'None declared'
                : plan.requiredPermissions.join(', '),
          ),
          if (plan.installActions.isNotEmpty) ...[
            const SizedBox(height: 8),
            Text(
              'Install actions',
              style: Theme.of(context).textTheme.labelLarge,
            ),
            const SizedBox(height: 6),
            ...plan.installActions.map(_installActionTile),
          ],
          ...plan.issues.map(_issueTile),
          const SizedBox(height: 8),
          FilledButton.icon(
            onPressed: plan.hasBlockingIssues || state.isBusy
                ? null
                : () => _confirm(
                    context,
                    title: 'Install ${plan.manifest.name}?',
                    message: _installConfirmationMessage(plan),
                    confirmLabel: 'Install',
                    action: () =>
                        _add(context, const PreviewedPackageInstalled()),
                  ),
            icon: const Icon(Icons.check),
            label: const Text('Install Plan'),
          ),
        ],
      ),
    );
  }
}

String _installConfirmationMessage(PackageInstallPlan plan) {
  final actions = plan.installActions.length;
  final capabilities = plan.requiredPermissions.isEmpty
      ? 'No runtime capabilities are declared.'
      : 'Declared runtime capabilities: ${plan.requiredPermissions.join(', ')}.';
  return 'This will install or enable $actions package${actions == 1 ? '' : 's'}. '
      '$capabilities Only install packages from authors you trust.';
}

Widget _installActionTile(PackageInstallAction action) {
  return Padding(
    padding: const EdgeInsets.only(bottom: 6),
    child: Row(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        Icon(
          action.enableOnly
              ? Icons.play_arrow
              : action.root
              ? Icons.archive
              : Icons.account_tree,
          size: 16,
        ),
        const SizedBox(width: 6),
        Expanded(
          child: Text(
            [
              '${action.name} ${action.version}',
              if (action.enableOnly) 'enable installed dependency',
              if (action.sourceName.isNotEmpty) action.sourceName,
              if (action.isRemote) 'remote',
              if (action.packageSha256.isNotEmpty) 'sha256 ready',
            ].join('  '),
            overflow: TextOverflow.ellipsis,
            style: const TextStyle(fontSize: 12),
          ),
        ),
      ],
    ),
  );
}

class _ModDetail extends StatelessWidget {
  const _ModDetail({required this.state, required this.mod});

  final LauncherState state;
  final InstalledMod mod;

  @override
  Widget build(BuildContext context) {
    final manifest = mod.manifest;
    final update = _availableUpdateForMod(state, mod);
    final issues = [
      ...mod.errors.map(
        (error) => LauncherIssue(severity: IssueSeverity.error, message: error),
      ),
      ..._issuesForMod(state, mod.id),
    ];
    return ListView(
      key: const Key('mod-detail-list'),
      padding: EdgeInsets.zero,
      children: [
        Text(mod.name, style: Theme.of(context).textTheme.titleMedium),
        const SizedBox(height: 8),
        Wrap(
          spacing: 6,
          runSpacing: 6,
          children: [
            StatusPill(
              label: mod.enabled ? 'Enabled' : 'Disabled',
              tone: mod.enabled ? StatusTone.good : StatusTone.neutral,
              icon: mod.enabled ? Icons.check_circle : Icons.pause_circle,
              tooltip: mod.enabled
                  ? 'This mod is included in dependency resolution and load order.'
                  : 'This mod is installed but excluded from load order.',
            ),
            if (update != null)
              StatusPill(
                label: 'Update available',
                tone: StatusTone.warning,
                icon: Icons.system_update_alt,
                tooltip: _updateTooltip(update),
              ),
            if (mod.restartRequired)
              _restartPill(context, state, label: 'Restart required'),
            if (mod.uninstallPending)
              const StatusPill(
                label: 'Uninstall pending',
                tone: StatusTone.warning,
                icon: Icons.delete_sweep,
                tooltip:
                    'Package files will be removed when TopiaForge next starts.',
              ),
          ],
        ),
        const SizedBox(height: 12),
        Wrap(
          spacing: 8,
          runSpacing: 8,
          children: [
            if (mod.repairableVersion case final repairVersion?)
              FilledButton.icon(
                onPressed: state.isBusy
                    ? null
                    : () => _confirm(
                        context,
                        title: 'Repair ${mod.name} $repairVersion?',
                        message:
                            'TopiaForge will reinstall version $repairVersion from a verified source, revalidate it, and replace missing or modified files.',
                        confirmLabel: 'Repair',
                        action: () =>
                            _add(context, const SelectedModRepairRequested()),
                      ),
                icon: const Icon(Icons.build_circle),
                label: const Text('Repair / Reinstall'),
              ),
            if (update != null)
              OutlinedButton.icon(
                onPressed: _canPreviewRegistryPackage(state, update)
                    ? () => _previewRegistryPackage(context, update)
                    : null,
                icon: const Icon(Icons.system_update_alt),
                label: const Text('Preview Update'),
              ),
            FilledButton.icon(
              onPressed: state.isBusy
                  ? null
                  : () =>
                        _add(context, SelectedModEnabledChanged(!mod.enabled)),
              icon: Icon(mod.enabled ? Icons.pause : Icons.play_arrow),
              label: Text(mod.enabled ? 'Disable' : 'Enable'),
            ),
            OutlinedButton.icon(
              onPressed: state.isBusy
                  ? null
                  : () => _confirm(
                      context,
                      title: 'Uninstall ${mod.name}?',
                      message: 'The installed package files will be removed.',
                      action: () =>
                          _add(context, const SelectedModUninstalled()),
                    ),
              icon: const Icon(Icons.delete),
              label: const Text('Uninstall'),
            ),
          ],
        ),
        const SizedBox(height: 12),
        _keyValue('ID', mod.id),
        _keyValue('Version', mod.version),
        if (mod.requestedVersion.isNotEmpty)
          _keyValue('Requested', mod.requestedVersion),
        if (mod.selectionReason.isNotEmpty)
          _keyValue('Selection', mod.selectionReason),
        if (update != null) _keyValue('Latest', update.manifest.version),
        _keyValue('Package', mod.packagePath),
        if (mod.installedVersions.isNotEmpty) ...[
          const SizedBox(height: 6),
          Text(
            'Installed versions',
            style: Theme.of(context).textTheme.labelLarge,
          ),
          const SizedBox(height: 6),
          ...mod.installedVersions.map(_installedVersionTile),
        ],
        if (manifest != null) ...[
          _keyValue(
            'Author',
            manifest.author.name.isEmpty ? 'Unknown' : manifest.author.name,
          ),
          _keyValue(
            'License',
            manifest.license.isEmpty ? 'Unspecified' : manifest.license,
          ),
          _keyValue(
            'Category',
            manifest.category.isEmpty ? 'Uncategorized' : manifest.category,
          ),
          if (manifest.description.isNotEmpty)
            Padding(
              padding: const EdgeInsets.only(top: 8),
              child: Text(manifest.description),
            ),
        ],
        if (issues.isNotEmpty)
          ...issues.map(_issueTile)
        else
          Text(
            'No dependency or manifest issues.',
            style: Theme.of(context).textTheme.bodySmall,
          ),
      ],
    );
  }
}
