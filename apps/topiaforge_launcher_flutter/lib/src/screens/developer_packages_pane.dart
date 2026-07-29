part of '../screens.dart';

// VCC "Manage Project" for a Unity project: installed (resolved) VPM packages, available packages to add, and
// the subscribed repositories.
class _PackagesPane extends StatelessWidget {
  const _PackagesPane({required this.state});

  final LauncherState state;

  @override
  Widget build(BuildContext context) {
    final busy = state.isBusy;
    final installedIds = state.unityResolved.map((p) => p.id).toSet();
    final addable = state.unityAvailable
        .where((info) => !installedIds.contains(info.name))
        .toList();

    return BorderedPane(
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Row(
            children: [
              Expanded(
                child: Text(
                  'Packages (Unity VPM)',
                  style: Theme.of(context).textTheme.titleMedium,
                ),
              ),
              FilledButton.icon(
                onPressed: busy
                    ? null
                    : () => _add(context, const DeveloperUnityResolved()),
                icon: const Icon(Icons.sync, size: 18),
                label: const Text('Resolve All'),
              ),
            ],
          ),
          const SizedBox(height: 10),

          const Text(
            'Installed',
            style: TextStyle(fontWeight: FontWeight.w600, fontSize: 13),
          ),
          if (state.unityResolved.isEmpty)
            const Text(
              'No packages resolved yet. Resolve, or add one below.',
              style: TextStyle(
                color: TopiaForgePalette.mutedText,
                fontSize: 12,
              ),
            )
          else
            ...state.unityResolved.map(
              (package) => ListTile(
                dense: true,
                contentPadding: EdgeInsets.zero,
                leading: const Icon(Icons.inventory_2_outlined, size: 18),
                title: Text(
                  package.displayName.isEmpty
                      ? package.id
                      : package.displayName,
                  overflow: TextOverflow.ellipsis,
                ),
                subtitle: Text('${package.id} ${package.version}'),
                trailing: OutlinedButton(
                  onPressed: busy
                      ? null
                      : () => _add(
                          context,
                          DeveloperUnityPackageRemoved(package.id),
                        ),
                  child: const Text('Remove'),
                ),
              ),
            ),

          const SizedBox(height: 12),
          const Text(
            'Available',
            style: TextStyle(fontWeight: FontWeight.w600, fontSize: 13),
          ),
          if (addable.isEmpty)
            const Text(
              'No additional packages available. Subscribe to a repository below, or run '
              'topiaforge unity pack-packages to build the local listing.',
              style: TextStyle(
                color: TopiaForgePalette.mutedText,
                fontSize: 12,
              ),
            )
          else
            ...addable.map(
              (info) => ListTile(
                dense: true,
                contentPadding: EdgeInsets.zero,
                leading: const Icon(Icons.extension_outlined, size: 18),
                title: Text(info.label, overflow: TextOverflow.ellipsis),
                subtitle: Text('${info.name} ${info.version}'),
                trailing: FilledButton.tonal(
                  onPressed: busy
                      ? null
                      : () => _add(
                          context,
                          DeveloperUnityPackageAdded(
                            info.name,
                            versionRange: '>=${info.version}',
                          ),
                        ),
                  child: const Text('Add'),
                ),
              ),
            ),

          const Divider(height: 24),
          Row(
            children: [
              const Expanded(
                child: Text(
                  'Repositories',
                  style: TextStyle(fontWeight: FontWeight.w600, fontSize: 13),
                ),
              ),
              TextButton.icon(
                onPressed: busy ? null : () => _showAddVpmRepoDialog(context),
                icon: const Icon(Icons.add, size: 16),
                label: const Text('Add repo'),
              ),
            ],
          ),
          ...state.unityRepos.map(
            (repo) => ListTile(
              dense: true,
              contentPadding: EdgeInsets.zero,
              leading: Icon(
                repo.builtIn ? Icons.lock_outline : Icons.cloud_outlined,
                size: 16,
              ),
              title: Text(repo.name, overflow: TextOverflow.ellipsis),
              subtitle: Text(repo.url, overflow: TextOverflow.ellipsis),
              trailing: repo.builtIn
                  ? null
                  : IconButton(
                      tooltip: 'Unsubscribe',
                      onPressed: busy
                          ? null
                          : () => _add(
                              context,
                              DeveloperUnityRepoRemoved(repo.id),
                            ),
                      icon: const Icon(Icons.link_off, size: 16),
                    ),
            ),
          ),
        ],
      ),
    );
  }
}
