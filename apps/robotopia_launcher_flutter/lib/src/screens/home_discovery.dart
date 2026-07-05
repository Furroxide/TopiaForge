part of '../screens.dart';

class _DiscoverRail extends StatelessWidget {
  const _DiscoverRail({required this.state});

  final LauncherState state;

  @override
  Widget build(BuildContext context) {
    final notInstalled = state.registryMods.where((mod) => !mod.isInstalled);
    final withUpdates = state.registryMods.where(
      (mod) => mod.isInstalled && mod.updateAvailable,
    );
    final rest = state.registryMods.where(
      (mod) => mod.isInstalled && !mod.updateAvailable,
    );
    final picks = _registryModsForDiscovery(
      state,
      mods: [...notInstalled, ...withUpdates, ...rest],
    ).take(6).toList();

    return Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        Row(
          children: [
            Text(
              'Discover mods',
              style: Theme.of(context).textTheme.titleLarge,
            ),
            const Spacer(),
            TextButton.icon(
              onPressed: () => _add(
                context,
                const LauncherSectionSelected(LauncherSection.browse),
              ),
              icon: const Icon(Icons.travel_explore),
              label: const Text('Browse all'),
            ),
          ],
        ),
        const SizedBox(height: 12),
        if (picks.isEmpty)
          _FindFirstModCard(state: state)
        else
          SizedBox(
            height: 200,
            child: ListView(
              scrollDirection: Axis.horizontal,
              children: [
                for (final mod in picks) _DiscoverCard(state: state, mod: mod),
              ],
            ),
          ),
      ],
    );
  }
}

class _DiscoverCard extends StatelessWidget {
  const _DiscoverCard({required this.state, required this.mod});

  final LauncherState state;
  final RegistryMod mod;

  @override
  Widget build(BuildContext context) {
    final manifest = mod.manifest;
    final category = manifest.category.isEmpty ? 'Mod' : manifest.category;
    final description = manifest.description.isNotEmpty
        ? manifest.description
        : 'From ${mod.sourceName.isEmpty ? 'the registry' : mod.sourceName}.';
    final canInstall = _canPreviewRegistryPackage(state, mod);

    return Padding(
      padding: const EdgeInsets.only(right: 12, bottom: 8),
      child: HoverLift(
        child: Container(
          width: 252,
          padding: const EdgeInsets.fromLTRB(16, 14, 16, 12),
          decoration: BoxDecoration(
            color: QuantumWorksPalette.surface,
            borderRadius: BorderRadius.circular(22),
            border: Border.all(
              color: QuantumWorksPalette.surfaceTint,
              width: 2,
            ),
            boxShadow: const [
              BoxShadow(
                color: Color(0x14168E96),
                offset: Offset(-3, 5),
                blurRadius: 0,
              ),
            ],
          ),
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              Row(
                children: [
                  Flexible(
                    child: Container(
                      padding: const EdgeInsets.symmetric(
                        horizontal: 10,
                        vertical: 3,
                      ),
                      decoration: BoxDecoration(
                        borderRadius: BorderRadius.circular(999),
                        border: Border.all(
                          color: QuantumWorksPalette.accentDark,
                          width: 1.5,
                        ),
                        color: const Color(0x1420F6FE),
                      ),
                      child: Text(
                        category.toUpperCase(),
                        overflow: TextOverflow.ellipsis,
                        style: const TextStyle(
                          color: QuantumWorksPalette.accentDark,
                          fontSize: 10,
                          fontWeight: FontWeight.w900,
                          letterSpacing: 1.2,
                        ),
                      ),
                    ),
                  ),
                  const Spacer(),
                  Text(
                    'v${manifest.version}',
                    style: Theme.of(context).textTheme.bodySmall,
                  ),
                ],
              ),
              const SizedBox(height: 10),
              Text(
                manifest.name,
                style: Theme.of(context).textTheme.titleMedium,
                maxLines: 1,
                overflow: TextOverflow.ellipsis,
              ),
              const SizedBox(height: 4),
              Expanded(
                child: Text(
                  description,
                  style: Theme.of(context).textTheme.bodySmall,
                  maxLines: 3,
                  overflow: TextOverflow.ellipsis,
                ),
              ),
              const SizedBox(height: 8),
              Row(
                children: [
                  if (mod.updateAvailable)
                    FilledButton.icon(
                      onPressed: canInstall
                          ? () => _previewRegistryPackage(
                              context,
                              mod,
                              switchToMods: true,
                            )
                          : null,
                      icon: const Icon(Icons.system_update_alt, size: 18),
                      label: const Text('Update'),
                    )
                  else if (mod.isInstalled)
                    const StatusPill(
                      label: 'Installed',
                      tone: StatusTone.good,
                      icon: Icons.check_circle,
                    )
                  else
                    FilledButton.icon(
                      onPressed: canInstall
                          ? () => _previewRegistryPackage(
                              context,
                              mod,
                              switchToMods: true,
                            )
                          : null,
                      icon: const Icon(Icons.download, size: 18),
                      label: const Text('Get'),
                    ),
                ],
              ),
            ],
          ),
        ),
      ),
    );
  }
}

class _FindFirstModCard extends StatelessWidget {
  const _FindFirstModCard({required this.state});

  final LauncherState state;

  @override
  Widget build(BuildContext context) {
    return BorderedPane(
      accentColor: QuantumWorksPalette.accentDark,
      padding: const EdgeInsets.fromLTRB(22, 18, 22, 18),
      child: Row(
        children: [
          Image.asset(
            QuantumWorksBrandAssets.robot,
            package: QuantumWorksBrandAssets.package,
            width: 84,
            fit: BoxFit.contain,
            filterQuality: FilterQuality.medium,
          ),
          const SizedBox(width: 18),
          Expanded(
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Text(
                  'Find your first mod',
                  style: Theme.of(context).textTheme.titleLarge,
                ),
                const SizedBox(height: 4),
                Text(
                  'Browse community-made mods and install them with one '
                  'click — the launcher handles the rest.',
                  style: Theme.of(context).textTheme.bodyMedium,
                ),
              ],
            ),
          ),
          const SizedBox(width: 18),
          FilledButton.icon(
            onPressed: () => _add(
              context,
              const LauncherSectionSelected(LauncherSection.browse),
            ),
            icon: const Icon(Icons.travel_explore),
            label: const Text('Open Browse'),
          ),
        ],
      ),
    );
  }
}

/// The very first thing a new user sees: no game detected yet, so everything
/// funnels into one friendly action.
