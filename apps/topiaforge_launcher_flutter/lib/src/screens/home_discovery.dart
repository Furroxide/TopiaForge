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
        Wrap(
          alignment: WrapAlignment.spaceBetween,
          crossAxisAlignment: WrapCrossAlignment.center,
          spacing: 12,
          runSpacing: 4,
          children: [
            Text(
              'Discover mods',
              style: Theme.of(context).textTheme.titleLarge,
            ),
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
            height:
                200 +
                100 *
                    (MediaQuery.textScalerOf(
                          context,
                        ).scale(1).clamp(1.0, 2.0).toDouble() -
                        1),
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
    final textScale = MediaQuery.textScalerOf(
      context,
    ).scale(1).clamp(1.0, 2.0).toDouble();
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
          width: 252 + 110 * (textScale - 1),
          padding: const EdgeInsets.fromLTRB(16, 14, 16, 12),
          decoration: BoxDecoration(
            color: TopiaForgePalette.surface,
            borderRadius: BorderRadius.circular(22),
            border: Border.all(color: TopiaForgePalette.surfaceTint, width: 2),
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
                          color: TopiaForgePalette.accentDark,
                          width: 1.5,
                        ),
                        color: const Color(0x1420F6FE),
                      ),
                      child: Text(
                        category.toUpperCase(),
                        overflow: TextOverflow.ellipsis,
                        style: const TextStyle(
                          color: TopiaForgePalette.accentDark,
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
    return LayoutBuilder(
      builder: (context, constraints) {
        final stacked =
            constraints.maxWidth < 760 ||
            MediaQuery.textScalerOf(context).scale(1) > 1.3;
        final image = Image.asset(
          TopiaForgeBrandAssets.robot,
          package: TopiaForgeBrandAssets.package,
          width: stacked ? 64 : 84,
          fit: BoxFit.contain,
          filterQuality: FilterQuality.medium,
        );
        final copy = Column(
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
        );
        final action = FilledButton.icon(
          onPressed: () => _add(
            context,
            const LauncherSectionSelected(LauncherSection.browse),
          ),
          icon: const Icon(Icons.travel_explore),
          label: const Text('Open Browse'),
        );
        return BorderedPane(
          accentColor: TopiaForgePalette.accentDark,
          padding: const EdgeInsets.fromLTRB(22, 18, 22, 18),
          child: stacked
              ? Column(
                  crossAxisAlignment: CrossAxisAlignment.start,
                  children: [
                    image,
                    const SizedBox(height: 12),
                    copy,
                    const SizedBox(height: 16),
                    action,
                  ],
                )
              : Row(
                  children: [
                    image,
                    const SizedBox(width: 18),
                    Expanded(child: copy),
                    const SizedBox(width: 18),
                    action,
                  ],
                ),
        );
      },
    );
  }
}
