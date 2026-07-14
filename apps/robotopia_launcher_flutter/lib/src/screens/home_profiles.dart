part of '../screens.dart';

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
        Wrap(
          alignment: WrapAlignment.spaceBetween,
          crossAxisAlignment: WrapCrossAlignment.center,
          spacing: 12,
          runSpacing: 4,
          children: [
            Text('Jump back in', style: Theme.of(context).textTheme.titleLarge),
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
          height:
              148 +
              84 *
                  (MediaQuery.textScalerOf(
                        context,
                      ).scale(1).clamp(1.0, 2.0).toDouble() -
                      1),
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
    final textScale = MediaQuery.textScalerOf(
      context,
    ).scale(1).clamp(1.0, 2.0).toDouble();
    final safeMode = profile.launchSettings.safeMode;
    final modsLabel = profile.inheritManagerModState
        ? 'Current mod setup'
        : '${profile.enabledMods.length} '
              '${profile.enabledMods.length == 1 ? 'mod' : 'mods'}';
    final caption =
        '$modsLabel · ${_worldNameFor(state, profile.worldSelection)}';

    return Padding(
      padding: const EdgeInsets.only(right: 12, bottom: 8),
      child: HoverLift(
        child: Container(
          width: 236 + 100 * (textScale - 1),
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
                    onPressed: state.canStartLaunchFlow && !state.isBusy
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
