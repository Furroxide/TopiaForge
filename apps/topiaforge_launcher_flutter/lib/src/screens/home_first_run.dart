part of '../screens.dart';

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
                      color: TopiaForgePalette.launch,
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
                              const TopiaForgeLogo(height: 44),
                              const SizedBox(height: 22),
                              Text(
                                'Welcome to TopiaForge modding',
                                textAlign: TextAlign.center,
                                style: Theme.of(context)
                                    .textTheme
                                    .headlineSmall!
                                    .copyWith(
                                      fontSize: 36,
                                      color: TopiaForgePalette.white,
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
                      TopiaForgeBrandAssets.babyStitch,
                      package: TopiaForgeBrandAssets.package,
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
            border: Border.all(color: TopiaForgePalette.accent, width: 2),
          ),
          child: Text(
            number,
            style: const TextStyle(
              color: TopiaForgePalette.accent,
              fontSize: 12,
              fontWeight: FontWeight.w900,
            ),
          ),
        ),
        const SizedBox(width: 8),
        Text(
          label,
          style: const TextStyle(
            color: TopiaForgePalette.white,
            fontSize: 13,
            fontWeight: FontWeight.w700,
          ),
        ),
      ],
    );
  }
}
