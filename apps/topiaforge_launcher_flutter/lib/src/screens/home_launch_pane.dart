part of '../screens.dart';

class _HeroLaunchPane extends StatelessWidget {
  const _HeroLaunchPane({required this.state});

  final LauncherState state;

  @override
  Widget build(BuildContext context) {
    final install = state.gameInstall!;
    final profile = state.selectedProfile;
    final safeMode = profile?.launchSettings.safeMode == true;
    final modCount = safeMode
        ? 0
        : profile != null && !profile.inheritManagerModState
        ? profile.enabledMods.length
        : state.resolution.orderedMods.length;

    final (String headline, String subline) = state.isBusy
        ? ('Working on it…', state.statusMessage)
        : !state.canLaunch
        ? ('Almost ready', 'One quick fix and TopiaForge is good to go.')
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
            border: Border.all(color: TopiaForgePalette.launch, width: 3),
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
                          TopiaForgePalette.darkPanel,
                          TopiaForgePalette.logPanel,
                        ],
                      ),
                    ),
                  ),
                ),
                Positioned.fill(
                  child: Image.asset(
                    TopiaForgeBrandAssets.cityHeader,
                    package: TopiaForgeBrandAssets.package,
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
                                color: TopiaForgePalette.white,
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
                            onPressed: state.canStartLaunchFlow && !state.isBusy
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
          child: IgnorePointer(child: const TopiaForgePixelRobot(width: 140)),
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
          color: TopiaForgePalette.accent.withValues(alpha: 0.7),
          width: 2,
        ),
        color: const Color(0x1420F6FE),
      ),
      child: Text(
        label,
        style: const TextStyle(
          color: TopiaForgePalette.accent,
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
                color: TopiaForgePalette.mutedText,
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
