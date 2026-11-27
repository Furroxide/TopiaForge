part of '../screens.dart';

/// The consent sheet's red developer-mode section, for the mods only the
/// host has: what ticking its box allows, in the approved wording
/// ([JoinHostServedText]), and the box, which Join waits for. The
/// box starts clear on every consent, and the bloc keeps it
/// ([JoinFlow.hostServedTicked]).
class _HostServedSection extends StatelessWidget {
  const _HostServedSection({required this.flow});

  final JoinFlow flow;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final ticked = flow.hostServedTicked;
    // A Material rather than a decoration, so the box's ink shows on it.
    return Semantics(
      container: true,
      child: Material(
        key: const Key('join-host-served'),
        color: TopiaForgePalette.danger.withValues(alpha: 0.08),
        clipBehavior: Clip.antiAlias,
        shape: RoundedRectangleBorder(
          borderRadius: BorderRadius.circular(16),
          side: BorderSide(
            color: TopiaForgePalette.danger.withValues(alpha: 0.7),
            width: 2,
          ),
        ),
        child: Padding(
          padding: const EdgeInsets.fromLTRB(12, 10, 12, 4),
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.stretch,
            mainAxisSize: MainAxisSize.min,
            children: [
              Row(
                crossAxisAlignment: CrossAxisAlignment.start,
                children: [
                  const Padding(
                    padding: EdgeInsets.only(top: 1, right: 10),
                    child: Icon(
                      Icons.gpp_maybe_outlined,
                      size: 18,
                      color: TopiaForgePalette.danger,
                    ),
                  ),
                  Expanded(
                    child: Column(
                      crossAxisAlignment: CrossAxisAlignment.start,
                      children: [
                        Text(
                          JoinHostServedText.title,
                          style: theme.textTheme.titleSmall,
                        ),
                        const SizedBox(height: 4),
                        Text(
                          JoinHostServedText.warning,
                          style: theme.textTheme.bodyMedium?.copyWith(
                            color: TopiaForgePalette.text,
                          ),
                        ),
                      ],
                    ),
                  ),
                ],
              ),
              const SizedBox(height: 4),
              CheckboxListTile(
                key: const Key('join-host-served-box'),
                value: ticked,
                onChanged: (value) => _add(
                  context,
                  MultiplayerJoinHostServedTicked(
                    flow: flow.id,
                    consent: flow.join!,
                    ticked: value ?? false,
                  ),
                ),
                controlAffinity: ListTileControlAffinity.leading,
                contentPadding: EdgeInsets.zero,
                dense: true,
                activeColor: TopiaForgePalette.danger,
                title: Text(
                  JoinHostServedText.checkbox,
                  style: theme.textTheme.titleSmall,
                ),
                subtitle: ticked
                    ? null
                    : const Text(JoinHostServedText.untickedHint),
              ),
            ],
          ),
        ),
      ),
    );
  }
}
