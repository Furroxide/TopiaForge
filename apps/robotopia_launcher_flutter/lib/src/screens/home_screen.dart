part of '../screens.dart';

/// The launcher's landing screen: a mission-control style launch pad with the
/// primary Launch action, a friendly systems check, quick profile switching,
/// and a discovery rail into the mod registry. Detailed launch configuration
/// (runtime repair, world selection, load order) lives on the Setup screen.
class HomeScreen extends StatelessWidget {
  const HomeScreen({super.key, required this.state});

  final LauncherState state;

  @override
  Widget build(BuildContext context) {
    if (state.gameInstall == null) {
      return _HomeFirstRun(state: state);
    }
    return SingleChildScrollView(
      // Top padding leaves room for the mascot overhanging the hero pane.
      padding: const EdgeInsets.fromLTRB(24, 44, 24, 24),
      child: Center(
        child: ConstrainedBox(
          constraints: const BoxConstraints(maxWidth: 1180),
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              StaggeredReveal(index: 0, child: _HeroLaunchPane(state: state)),
              const SizedBox(height: 24),
              StaggeredReveal(index: 1, child: _JumpBackInRow(state: state)),
              const SizedBox(height: 24),
              StaggeredReveal(index: 2, child: _DiscoverRail(state: state)),
            ],
          ),
        ),
      ),
    );
  }
}

String _worldNameFor(LauncherState state, WorldSelection selection) {
  final worlds = state.worldCatalog.worlds;
  for (final world in worlds) {
    if (world.id == selection.worldId) {
      return world.name;
    }
  }
  return worlds.isEmpty ? 'Default world' : worlds.first.name;
}

int _updatesAvailable(LauncherState state) {
  return state.registryMods.where((mod) => mod.updateAvailable).length;
}

class _HeroLaunchPane extends StatelessWidget {
  const _HeroLaunchPane({required this.state});

  final LauncherState state;

  @override
  Widget build(BuildContext context) {
    final install = state.gameInstall!;
    final profile = state.selectedProfile;
    final safeMode = profile?.launchSettings.safeMode == true;
    final modCount = state.resolution.orderedMods.length;

    final (String headline, String subline) = state.isBusy
        ? ('Working on it…', state.statusMessage)
        : !state.canLaunch
        ? ('Almost ready', 'One quick fix and Robotopia is good to go.')
        : (
            'Ready for liftoff',
            '${profile?.name ?? 'Default'} profile · '
                '$modCount ${modCount == 1 ? 'mod' : 'mods'} enabled · '
                '${_worldNameFor(state, profile?.worldSelection ?? const WorldSelection())}',
          );

    return Stack(
      clipBehavior: Clip.none,
      children: [
        Container(
          decoration: BoxDecoration(
            borderRadius: BorderRadius.circular(28),
            border: Border.all(color: QuantumWorksPalette.launch, width: 3),
            boxShadow: [
              const BoxShadow(
                color: Color(0x66CC620E),
                offset: Offset(-4, 8),
                blurRadius: 0,
              ),
              BoxShadow(
                color: Colors.black.withValues(alpha: 0.18),
                offset: const Offset(0, 14),
                blurRadius: 34,
              ),
            ],
          ),
          child: ClipRRect(
            borderRadius: BorderRadius.circular(25),
            child: Stack(
              children: [
                const Positioned.fill(
                  child: DecoratedBox(
                    decoration: BoxDecoration(
                      gradient: LinearGradient(
                        begin: Alignment.topLeft,
                        end: Alignment.bottomRight,
                        colors: [
                          QuantumWorksPalette.darkPanel,
                          QuantumWorksPalette.logPanel,
                        ],
                      ),
                    ),
                  ),
                ),
                Positioned.fill(
                  child: Image.asset(
                    QuantumWorksBrandAssets.cityHeader,
                    package: QuantumWorksBrandAssets.package,
                    fit: BoxFit.cover,
                    alignment: Alignment.bottomCenter,
                    opacity: const AlwaysStoppedAnimation(0.16),
                  ),
                ),
                Positioned.fill(
                  child: CustomPaint(painter: _HeroGridPainter()),
                ),
                Padding(
                  padding: const EdgeInsets.fromLTRB(30, 26, 30, 24),
                  child: Column(
                    crossAxisAlignment: CrossAxisAlignment.start,
                    children: [
                      const _EyebrowChip(label: 'MISSION CONTROL'),
                      const SizedBox(height: 18),
                      Padding(
                        // Keep the headline clear of the overhanging mascot.
                        padding: const EdgeInsets.only(right: 130),
                        child: Text(
                          headline,
                          style: Theme.of(context).textTheme.headlineSmall!
                              .copyWith(
                                fontSize: 40,
                                color: QuantumWorksPalette.white,
                              ),
                        ),
                      ),
                      const SizedBox(height: 8),
                      Text(
                        subline,
                        style: Theme.of(context).textTheme.bodyMedium!.copyWith(
                          color: const Color(0xCCFFFFFF),
                        ),
                      ),
                      const SizedBox(height: 24),
                      Wrap(
                        spacing: 14,
                        runSpacing: 12,
                        crossAxisAlignment: WrapCrossAlignment.center,
                        children: [
                          GlowButton(
                            label: safeMode ? 'Launch Safe' : 'Launch',
                            icon: Icons.rocket_launch,
                            onPressed: state.canLaunch && !state.isBusy
                                ? () =>
                                      _add(context, const GameLaunchRequested())
                                : null,
                          ),
                          TextButton.icon(
                            style: TextButton.styleFrom(
                              foregroundColor: const Color(0xB3FFFFFF),
                            ),
                            onPressed: () => _add(
                              context,
                              const LauncherSectionSelected(
                                LauncherSection.setup,
                              ),
                            ),
                            icon: const Icon(Icons.tune),
                            label: const Text('Launch options'),
                          ),
                        ],
                      ),
                      const SizedBox(height: 24),
                      _SystemsCheckStrip(state: state, install: install),
                    ],
                  ),
                ),
              ],
            ),
          ),
        ),
        Positioned(
          right: 18,
          top: -34,
          child: IgnorePointer(
            child: Image.asset(
              QuantumWorksBrandAssets.robot,
              package: QuantumWorksBrandAssets.package,
              width: 140,
              fit: BoxFit.contain,
              filterQuality: FilterQuality.medium,
            ),
          ),
        ),
      ],
    );
  }
}

class _EyebrowChip extends StatelessWidget {
  const _EyebrowChip({required this.label});

  final String label;

  @override
  Widget build(BuildContext context) {
    return Container(
      padding: const EdgeInsets.symmetric(horizontal: 12, vertical: 5),
      decoration: BoxDecoration(
        borderRadius: BorderRadius.circular(999),
        border: Border.all(
          color: QuantumWorksPalette.accent.withValues(alpha: 0.7),
          width: 2,
        ),
        color: const Color(0x1420F6FE),
      ),
      child: Text(
        label,
        style: const TextStyle(
          color: QuantumWorksPalette.accent,
          fontSize: 11,
          fontWeight: FontWeight.w900,
          letterSpacing: 2.4,
        ),
      ),
    );
  }
}

/// Friendly, jargon-free status readout. Every pill states an outcome, not a
/// component name; the runtime pill doubles as the one-click fix.
class _SystemsCheckStrip extends StatelessWidget {
  const _SystemsCheckStrip({required this.state, required this.install});

  final LauncherState state;
  final GameInstall install;

  @override
  Widget build(BuildContext context) {
    final runtimeReady =
        install.bepInExStatus == ComponentState.ready &&
        install.loaderStatus == ComponentState.ready;
    final runtimeMissing =
        install.bepInExStatus == ComponentState.missing ||
        install.loaderStatus == ComponentState.missing;
    final modCount = state.resolution.orderedMods.length;
    final updates = _updatesAvailable(state);

    return Container(
      padding: const EdgeInsets.symmetric(horizontal: 14, vertical: 12),
      decoration: BoxDecoration(
        color: const Color(0xEEFFF7E9),
        borderRadius: BorderRadius.circular(18),
      ),
      child: Wrap(
        spacing: 8,
        runSpacing: 8,
        crossAxisAlignment: WrapCrossAlignment.center,
        children: [
          const Padding(
            padding: EdgeInsets.only(right: 4),
            child: Text(
              'SYSTEMS CHECK',
              style: TextStyle(
                color: QuantumWorksPalette.mutedText,
                fontSize: 10,
                fontWeight: FontWeight.w900,
                letterSpacing: 1.8,
              ),
            ),
          ),
          const StatusPill(
            label: 'Game found',
            tone: StatusTone.good,
            icon: Icons.check_circle,
          ),
          if (runtimeReady)
            const StatusPill(
              label: 'Runtime ready',
              tone: StatusTone.good,
              icon: Icons.check_circle,
            )
          else
            StatusPill(
              label: 'Runtime needs a quick fix',
              tone: runtimeMissing ? StatusTone.danger : StatusTone.warning,
              icon: Icons.build,
              tooltip: 'One click installs the game runtime pieces for you.',
              onPressed: state.isBusy
                  ? null
                  : () => _add(context, const RuntimeRepaired()),
            ),
          StatusPill(
            label: '$modCount ${modCount == 1 ? 'mod' : 'mods'} enabled',
            tone: StatusTone.info,
            icon: Icons.extension,
            onPressed: () => _add(
              context,
              const LauncherSectionSelected(LauncherSection.mods),
            ),
          ),
          if (updates > 0)
            StatusPill(
              label: updates == 1
                  ? '1 update available'
                  : '$updates updates available',
              tone: StatusTone.warning,
              icon: Icons.system_update_alt,
              tooltip: 'Newer versions of installed mods are in the registry.',
              onPressed: () => _add(
                context,
                const LauncherSectionSelected(LauncherSection.browse),
              ),
            ),
        ],
      ),
    );
  }
}

class _JumpBackInRow extends StatelessWidget {
  const _JumpBackInRow({required this.state});

  final LauncherState state;

  @override
  Widget build(BuildContext context) {
    final selectedId = state.selectedProfile?.id;
    final profiles = [
      ...state.profiles.where((profile) => profile.id == selectedId),
      ...state.profiles.where((profile) => profile.id != selectedId),
    ].take(4).toList();

    return Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        Row(
          children: [
            Text('Jump back in', style: Theme.of(context).textTheme.titleLarge),
            const Spacer(),
            TextButton.icon(
              onPressed: () => _add(
                context,
                const LauncherSectionSelected(LauncherSection.profiles),
              ),
              icon: const Icon(Icons.layers),
              label: const Text('Manage profiles'),
            ),
          ],
        ),
        const SizedBox(height: 12),
        SizedBox(
          height: 148,
          child: ListView(
            scrollDirection: Axis.horizontal,
            children: [
              for (final profile in profiles)
                _ProfileCard(
                  state: state,
                  profile: profile,
                  selected: profile.id == selectedId,
                ),
            ],
          ),
        ),
      ],
    );
  }
}

class _ProfileCard extends StatelessWidget {
  const _ProfileCard({
    required this.state,
    required this.profile,
    required this.selected,
  });

  final LauncherState state;
  final LauncherProfile profile;
  final bool selected;

  @override
  Widget build(BuildContext context) {
    final safeMode = profile.launchSettings.safeMode;
    final modsLabel = profile.enabledMods.isEmpty
        ? 'Current mod setup'
        : '${profile.enabledMods.length} '
              '${profile.enabledMods.length == 1 ? 'mod' : 'mods'}';
    final caption =
        '$modsLabel · ${_worldNameFor(state, profile.worldSelection)}';

    return Padding(
      padding: const EdgeInsets.only(right: 12, bottom: 8),
      child: HoverLift(
        child: Container(
          width: 236,
          padding: const EdgeInsets.fromLTRB(16, 14, 16, 12),
          decoration: BoxDecoration(
            color: QuantumWorksPalette.surface,
            borderRadius: BorderRadius.circular(22),
            border: Border.all(
              color: selected
                  ? QuantumWorksPalette.launch
                  : QuantumWorksPalette.surfaceTint,
              width: selected ? 2.5 : 2,
            ),
            boxShadow: const [
              BoxShadow(
                color: Color(0x14CC620E),
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
                  Icon(
                    safeMode ? Icons.health_and_safety : Icons.layers,
                    size: 18,
                    color: QuantumWorksPalette.launchDark,
                  ),
                  const SizedBox(width: 6),
                  Expanded(
                    child: Text(
                      profile.name,
                      style: Theme.of(context).textTheme.titleMedium,
                      overflow: TextOverflow.ellipsis,
                    ),
                  ),
                  if (selected) ...[
                    const SizedBox(width: 6),
                    const StatusPill(label: 'Active', tone: StatusTone.info),
                  ],
                ],
              ),
              const SizedBox(height: 4),
              Text(
                caption,
                style: Theme.of(context).textTheme.bodySmall,
                overflow: TextOverflow.ellipsis,
              ),
              const Spacer(),
              Row(
                children: [
                  FilledButton.icon(
                    onPressed: state.canLaunch && !state.isBusy
                        ? () =>
                              _add(context, ProfileLaunchRequested(profile.id))
                        : null,
                    icon: const Icon(Icons.play_arrow, size: 18),
                    label: const Text('Play'),
                  ),
                  const SizedBox(width: 6),
                  if (!selected)
                    TextButton(
                      onPressed: state.isBusy
                          ? null
                          : () => _add(context, ProfileSelected(profile.id)),
                      child: const Text('Use'),
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
class _HomeFirstRun extends StatelessWidget {
  const _HomeFirstRun({required this.state});

  final LauncherState state;

  @override
  Widget build(BuildContext context) {
    return Center(
      child: SingleChildScrollView(
        padding: const EdgeInsets.fromLTRB(24, 64, 24, 24),
        child: ConstrainedBox(
          constraints: const BoxConstraints(maxWidth: 760),
          child: StaggeredReveal(
            index: 0,
            child: Stack(
              clipBehavior: Clip.none,
              children: [
                Container(
                  decoration: BoxDecoration(
                    borderRadius: BorderRadius.circular(28),
                    border: Border.all(
                      color: QuantumWorksPalette.launch,
                      width: 3,
                    ),
                    boxShadow: [
                      const BoxShadow(
                        color: Color(0x66CC620E),
                        offset: Offset(-4, 8),
                        blurRadius: 0,
                      ),
                      BoxShadow(
                        color: Colors.black.withValues(alpha: 0.18),
                        offset: const Offset(0, 14),
                        blurRadius: 34,
                      ),
                    ],
                  ),
                  child: ClipRRect(
                    borderRadius: BorderRadius.circular(25),
                    child: Stack(
                      children: [
                        const Positioned.fill(
                          child: DecoratedBox(
                            decoration: BoxDecoration(
                              gradient: LinearGradient(
                                begin: Alignment.topLeft,
                                end: Alignment.bottomRight,
                                colors: [
                                  QuantumWorksPalette.darkPanel,
                                  QuantumWorksPalette.logPanel,
                                ],
                              ),
                            ),
                          ),
                        ),
                        Positioned.fill(
                          child: Image.asset(
                            QuantumWorksBrandAssets.cityHeader,
                            package: QuantumWorksBrandAssets.package,
                            fit: BoxFit.cover,
                            alignment: Alignment.bottomCenter,
                            opacity: const AlwaysStoppedAnimation(0.18),
                          ),
                        ),
                        Positioned.fill(
                          child: CustomPaint(painter: _HeroGridPainter()),
                        ),
                        Padding(
                          padding: const EdgeInsets.fromLTRB(36, 40, 36, 30),
                          child: Column(
                            children: [
                              const QuantumWorksLogo(height: 44),
                              const SizedBox(height: 22),
                              Text(
                                'Welcome to Robotopia modding',
                                textAlign: TextAlign.center,
                                style: Theme.of(context)
                                    .textTheme
                                    .headlineSmall!
                                    .copyWith(
                                      fontSize: 36,
                                      color: QuantumWorksPalette.white,
                                    ),
                              ),
                              const SizedBox(height: 10),
                              Text(
                                'Find community-made mods, install them with '
                                'one click, and launch straight into the game.',
                                textAlign: TextAlign.center,
                                style: Theme.of(context).textTheme.bodyMedium!
                                    .copyWith(color: const Color(0xCCFFFFFF)),
                              ),
                              const SizedBox(height: 28),
                              GlowButton(
                                label: 'Find My Game',
                                icon: Icons.search,
                                onPressed: state.isBusy
                                    ? null
                                    : () => _add(
                                        context,
                                        const KnownInstallDetected(),
                                      ),
                              ),
                              const SizedBox(height: 10),
                              TextButton(
                                style: TextButton.styleFrom(
                                  foregroundColor: const Color(0xB3FFFFFF),
                                ),
                                onPressed: state.isBusy
                                    ? null
                                    : () => _chooseGameFolder(context),
                                child: const Text('Choose the folder myself'),
                              ),
                              const SizedBox(height: 26),
                              const _StepStrip(),
                            ],
                          ),
                        ),
                      ],
                    ),
                  ),
                ),
                Positioned(
                  right: 6,
                  top: -44,
                  child: IgnorePointer(
                    child: Image.asset(
                      QuantumWorksBrandAssets.babyStitch,
                      package: QuantumWorksBrandAssets.package,
                      width: 128,
                      fit: BoxFit.contain,
                      filterQuality: FilterQuality.medium,
                    ),
                  ),
                ),
              ],
            ),
          ),
        ),
      ),
    );
  }
}

class _StepStrip extends StatelessWidget {
  const _StepStrip();

  @override
  Widget build(BuildContext context) {
    return Container(
      padding: const EdgeInsets.symmetric(horizontal: 16, vertical: 12),
      decoration: BoxDecoration(
        color: const Color(0x14FFFFFF),
        borderRadius: BorderRadius.circular(16),
      ),
      child: const Wrap(
        spacing: 22,
        runSpacing: 10,
        alignment: WrapAlignment.center,
        children: [
          _StepItem(number: '1', label: 'Find your game'),
          _StepItem(number: '2', label: 'Pick your mods'),
          _StepItem(number: '3', label: 'Hit Launch'),
        ],
      ),
    );
  }
}

class _StepItem extends StatelessWidget {
  const _StepItem({required this.number, required this.label});

  final String number;
  final String label;

  @override
  Widget build(BuildContext context) {
    return Row(
      mainAxisSize: MainAxisSize.min,
      children: [
        Container(
          width: 24,
          height: 24,
          alignment: Alignment.center,
          decoration: BoxDecoration(
            shape: BoxShape.circle,
            border: Border.all(color: QuantumWorksPalette.accent, width: 2),
          ),
          child: Text(
            number,
            style: const TextStyle(
              color: QuantumWorksPalette.accent,
              fontSize: 12,
              fontWeight: FontWeight.w900,
            ),
          ),
        ),
        const SizedBox(width: 8),
        Text(
          label,
          style: const TextStyle(
            color: QuantumWorksPalette.white,
            fontSize: 13,
            fontWeight: FontWeight.w700,
          ),
        ),
      ],
    );
  }
}

/// The launch-pad floor: the same perspective grid as the paper backdrop, but
/// in faint cyan against the hero pane's dark gradient.
class _HeroGridPainter extends CustomPainter {
  @override
  void paint(Canvas canvas, Size size) {
    final paint = Paint()
      ..color = const Color(0x1420F6FE)
      ..strokeWidth = 1;
    const spacing = 42.0;
    final horizon = size.height * 0.52;

    for (double y = horizon; y < size.height; y += spacing) {
      canvas.drawLine(Offset(0, y), Offset(size.width, y), paint);
    }
    for (double x = -size.width; x < size.width * 2; x += spacing) {
      canvas.drawLine(
        Offset(size.width / 2, horizon),
        Offset(x, size.height),
        paint,
      );
    }
  }

  @override
  bool shouldRepaint(covariant CustomPainter oldDelegate) => false;
}
