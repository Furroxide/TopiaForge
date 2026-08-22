part of '../screens.dart';

/// Developer-only NAT reachability probe.
///
/// Measures whether this machine could host a session other players reach directly. That fraction is the dominant
/// term in the multiplayer relay cost model and has never been measured — see
/// `docs/internal/LauncherReachabilityProbe.md`.
///
/// The pane is deliberately blunt about its own limits: off by default, developer-mode only, and results stay on
/// this machine because no approved privacy notice covers sharing them.
class _ReachabilityPane extends StatelessWidget {
  const _ReachabilityPane({required this.state});

  final LauncherState state;

  @override
  Widget build(BuildContext context) {
    final settings = state.reachabilityProbe;
    final result = state.reachabilityResult;
    final busy = state.isBusy;

    return BorderedPane(
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Row(
            children: [
              Expanded(
                child: Text(
                  'Network reachability probe',
                  style: Theme.of(context).textTheme.titleMedium,
                ),
              ),
              StatusPill(
                key: const Key('reachability-state-pill'),
                label: settings.enabled ? 'Enabled' : 'Off',
                tone: settings.enabled ? StatusTone.good : StatusTone.neutral,
                icon: settings.enabled
                    ? Icons.wifi_tethering
                    : Icons.wifi_tethering_off,
              ),
            ],
          ),
          const SizedBox(height: 10),
          const Text(
            'Classifies this machine’s NAT so we can find out how many players could host a session directly. '
            'It sends a few small UDP packets to configured measurement servers. It does not touch Robotopia, '
            'does not open a game session, and records no addresses — only the NAT category.',
            style: TextStyle(color: TopiaForgePalette.mutedText, fontSize: 12),
          ),
          const SizedBox(height: 12),
          SwitchListTile(
            key: const Key('reachability-enabled-switch'),
            contentPadding: EdgeInsets.zero,
            value: settings.enabled,
            title: const Text('Run the reachability probe'),
            subtitle: const Text(
              'Off by default. Nothing runs until you turn this on.',
            ),
            onChanged: busy
                ? null
                : (value) => _add(
                    context,
                    ReachabilityProbeSettingsChanged(
                      settings.copyWith(
                        enabled: value,
                        // Turning the probe off also withdraws sharing consent, so re-enabling never silently
                        // restores an agreement the player may have forgotten about.
                        shareAggregateResults: value
                            ? settings.shareAggregateResults
                            : false,
                      ),
                    ),
                  ),
          ),
          SwitchListTile(
            key: const Key('reachability-sharing-switch'),
            contentPadding: EdgeInsets.zero,
            value: settings.shareAggregateResults,
            title: const Text('Allow sharing the aggregate result'),
            subtitle: const Text(
              'Records your agreement. Nothing is sent: sharing also needs an approved privacy notice, '
              'which does not exist yet.',
            ),
            onChanged: busy || !settings.enabled
                ? null
                : (value) => _add(
                    context,
                    ReachabilityProbeSettingsChanged(
                      settings.copyWith(shareAggregateResults: value),
                    ),
                  ),
          ),
          const SizedBox(height: 8),
          Wrap(
            spacing: 10,
            runSpacing: 10,
            crossAxisAlignment: WrapCrossAlignment.center,
            children: [
              FilledButton.icon(
                key: const Key('reachability-run-button'),
                onPressed: busy || !settings.enabled
                    ? null
                    : () => _add(context, const ReachabilityProbeRequested()),
                icon: const Icon(Icons.network_check),
                label: const Text('Run probe'),
              ),
              if (result != null)
                StatusPill(
                  key: const Key('reachability-result-pill'),
                  label: _reachabilityLabel(result.reachability),
                  tone: result.reachability.requiresRelay
                      ? StatusTone.warning
                      : StatusTone.good,
                  icon: result.reachability.requiresRelay
                      ? Icons.alt_route
                      : Icons.check_circle,
                ),
            ],
          ),
          if (result != null) ...[
            const SizedBox(height: 10),
            Text(
              'Mapping: ${result.mapping.name} · filtering: ${result.filtering.name}',
              key: const Key('reachability-result-detail'),
              style: const TextStyle(
                color: TopiaForgePalette.mutedText,
                fontSize: 12,
              ),
            ),
          ],
        ],
      ),
    );
  }

  String _reachabilityLabel(HostReachability reachability) =>
      switch (reachability) {
        HostReachability.direct => 'Directly reachable',
        HostReachability.holePunchable => 'Reachable by hole punching',
        HostReachability.relayRequired => 'Needs a relay',
        HostReachability.udpBlocked => 'UDP blocked',
        HostReachability.unknown => 'Not classified',
      };
}
