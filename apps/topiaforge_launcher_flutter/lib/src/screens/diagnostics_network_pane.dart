part of '../screens.dart';

/// The explanation shown before the first LAN listen or port check, with the
/// ports a check opens: the one the player hosts on, and LAN discovery's.
NetworkCheckNotice _networkNotice(LauncherState state) => NetworkCheckNotice(
  gamePort:
      state.multiplayer.settings.preferences?.hosting.port ??
      LanBeacon.defaultGamePort,
  lanPort: LanBeacon.discoveryPort,
);

/// Diagnostics → Network: the multiplayer service, LAN discovery's state and
/// counters, and port checks, which run only when the player asks. Listening
/// and port checks open ports, so the first of either explains what it does
/// before it starts. Nothing here names an address: the report holds none.
class _NetworkDiagnosticsPane extends StatefulWidget {
  const _NetworkDiagnosticsPane({required this.state});

  final LauncherState state;

  @override
  State<_NetworkDiagnosticsPane> createState() =>
      _NetworkDiagnosticsPaneState();
}

class _NetworkDiagnosticsPaneState extends State<_NetworkDiagnosticsPane> {
  @override
  void initState() {
    super.initState();
    // Reading the service and LAN state opens nothing, so it runs at once.
    if (widget.state.multiplayer.network.report == null &&
        context.read<LauncherBloc>().hasMultiplayer) {
      _add(context, const MultiplayerNetworkCheckRequested());
    }
  }

  /// Runs [event] once the player has read the explanation, asking first.
  Future<void> _afterNotice(LauncherEvent event) async {
    final state = widget.state;
    final read =
        state.multiplayer.network.noticeRead ||
        state.multiplayer.settings.preferences?.needsNetworkNotice == false;
    if (!read) {
      if (!await _networkNotice(state).show(context, asking: true)) return;
      if (!mounted) return;
      _add(context, const MultiplayerNetworkNoticeRead());
    }
    if (mounted) _add(context, event);
  }

  @override
  Widget build(BuildContext context) {
    if (!context.read<LauncherBloc>().hasMultiplayer) {
      return const Padding(
        padding: EdgeInsets.all(20),
        child: SessionNotice(
          message: 'Network diagnostics are not available in this launcher.',
          tone: StatusTone.neutral,
        ),
      );
    }
    final network = widget.state.multiplayer.network;
    final report = network.report;
    final lan = network.lan ?? report?.lan;
    final listening = lan?.status == LanDiscoveryStatus.listening;
    return FocusTraversalGroup(
      child: SingleChildScrollView(
        key: const Key('network-diagnostics-scroll'),
        padding: const EdgeInsets.fromLTRB(20, 12, 20, 20),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.stretch,
          children: [
            Wrap(
              spacing: 8,
              runSpacing: 8,
              children: [
                OutlinedButton.icon(
                  key: const Key('network-refresh'),
                  onPressed: network.checking
                      ? null
                      : () => _add(
                          context,
                          const MultiplayerNetworkCheckRequested(),
                        ),
                  icon: const Icon(Icons.refresh),
                  label: const Text('Refresh'),
                ),
                OutlinedButton.icon(
                  key: const Key('network-listen'),
                  onPressed: () => network.listening || listening
                      ? _add(
                          context,
                          const MultiplayerLanListeningRequested(false),
                        )
                      : _afterNotice(
                          const MultiplayerLanListeningRequested(true),
                        ),
                  icon: Icon(
                    network.listening || listening ? Icons.stop : Icons.lan,
                  ),
                  label: Text(
                    network.listening || listening
                        ? 'Stop listening'
                        : 'Listen for LAN sessions',
                  ),
                ),
                FilledButton.icon(
                  key: const Key('network-check-ports'),
                  onPressed: network.checking
                      ? null
                      : () => _afterNotice(
                          const MultiplayerNetworkCheckRequested(
                            checkPorts: true,
                          ),
                        ),
                  icon: const Icon(Icons.settings_ethernet),
                  label: const Text('Check ports'),
                ),
                TextButton(
                  key: const Key('network-notice-again'),
                  onPressed: () =>
                      _networkNotice(widget.state).show(context, asking: false),
                  child: const Text('What these checks do'),
                ),
              ],
            ),
            const SizedBox(height: 12),
            if (network.checking) ...[
              Text(
                network.checkingPorts
                    ? 'Checking the ports…'
                    : 'Reading the network state…',
                style: Theme.of(context).textTheme.bodySmall,
              ),
              const SizedBox(height: 4),
              const LinearProgressIndicator(minHeight: 3),
              const SizedBox(height: 12),
            ],
            if (network.problem case final problem?) ...[
              SessionNotice(
                key: const Key('network-problem'),
                message: problem,
                tone: StatusTone.danger,
              ),
              const SizedBox(height: 12),
            ],
            if (report == null && !network.checking)
              const SessionNotice(
                key: Key('network-empty'),
                message: 'No network check has run yet. Press Refresh.',
                tone: StatusTone.neutral,
              )
            else if (report != null) ...[
              _ServicePane(service: report.service),
              const SizedBox(height: 14),
              _LanPane(lan: lan ?? report.lan),
              const SizedBox(height: 14),
              _PortsPane(report: report),
            ],
          ],
        ),
      ),
    );
  }
}

class _ServicePane extends StatelessWidget {
  const _ServicePane({required this.service});

  final MultiplayerServiceStatus service;

  @override
  Widget build(BuildContext context) {
    final (label, tone, line) = switch (service) {
      MultiplayerServiceStatus.unconfigured => (
        'Not in this build',
        StatusTone.neutral,
        'This build names no multiplayer service, so Internet browsing, '
            'invites and server links are unavailable. LAN and direct play '
            'work without it.',
      ),
      MultiplayerServiceStatus.invalid => (
        'Ignored',
        StatusTone.warning,
        'This build names a multiplayer service that is not a valid HTTPS '
            'origin, so it is ignored and nothing is contacted.',
      ),
      MultiplayerServiceStatus.configured => (
        'Configured',
        StatusTone.good,
        'This build names a multiplayer service for Internet browsing, '
            'invites and server links.',
      ),
    };
    return _SettingsGroup(
      title: 'Multiplayer service',
      icon: Icons.cloud_outlined,
      children: [
        StatusPill(key: const Key('network-service'), label: label, tone: tone),
        const SizedBox(height: 8),
        Text(line, style: Theme.of(context).textTheme.bodySmall),
      ],
    );
  }
}

class _LanPane extends StatelessWidget {
  const _LanPane({required this.lan});

  final LanBrowseSnapshot lan;

  @override
  Widget build(BuildContext context) {
    final (label, tone) = switch (lan.status) {
      LanDiscoveryStatus.listening => ('Listening', StatusTone.good),
      LanDiscoveryStatus.stopped => ('Not listening', StatusTone.neutral),
      LanDiscoveryStatus.disabled => ('Off in settings', StatusTone.neutral),
      LanDiscoveryStatus.unavailable => ('Unavailable', StatusTone.danger),
    };
    final counters = lan.counters;
    return _SettingsGroup(
      title: 'LAN discovery',
      icon: Icons.lan_outlined,
      children: [
        StatusPill(key: const Key('network-lan'), label: label, tone: tone),
        if (lan.message case final message?) ...[
          const SizedBox(height: 8),
          Text(message, style: Theme.of(context).textTheme.bodySmall),
        ],
        const SizedBox(height: 10),
        _keyValue('Sessions found', '${lan.listings.length}'),
        _keyValue('Waiting for info', '${lan.pending}'),
        _keyValue('Other versions', '${lan.incompatibleServerIds.length}'),
        _keyValue('Beyond this network', '${lan.beyondLocalNetwork}'),
        const Divider(),
        _keyValue('Datagrams heard', '${counters.datagramsHeard}'),
        _keyValue('Not beacons', '${counters.invalidBeacons}'),
        _keyValue('Queries sent', '${counters.queriesSent}'),
        _keyValue('Queries held back', '${counters.queriesThrottled}'),
        _keyValue('Queries not sent', '${counters.queriesUnsent}'),
        _keyValue('Replies accepted', '${counters.repliesAccepted}'),
        _keyValue('Replies refused', '${counters.repliesRefused}'),
        _keyValue('Unusable info', '${counters.unusableInfo}'),
        _keyValue('Sessions dropped', '${counters.serversDropped}'),
        _keyValue('Faults', '${counters.faults}'),
        _keyValue('Sockets replaced', '${counters.querySocketsReplaced}'),
      ],
    );
  }
}

class _PortsPane extends StatelessWidget {
  const _PortsPane({required this.report});

  final MultiplayerNetworkReport report;

  @override
  Widget build(BuildContext context) {
    Widget row(PortCheck check, String use) {
      final (label, tone) = switch (check.result) {
        PortCheckResult.notChecked => ('Not checked', StatusTone.neutral),
        PortCheckResult.available => ('Available', StatusTone.good),
        PortCheckResult.inUse => ('In use', StatusTone.warning),
        PortCheckResult.denied => ('Blocked', StatusTone.danger),
        PortCheckResult.failed => ('Failed', StatusTone.danger),
      };
      return Padding(
        padding: const EdgeInsets.only(bottom: 8),
        child: Wrap(
          spacing: 10,
          runSpacing: 4,
          crossAxisAlignment: WrapCrossAlignment.center,
          children: [
            SizedBox(
              width: 120,
              child: Text(
                '${check.protocol.name.toUpperCase()} '
                '${check.port == 0 ? 'any port' : check.port}',
              ),
            ),
            StatusPill(label: label, tone: tone),
            Text(use, style: Theme.of(context).textTheme.bodySmall),
          ],
        ),
      );
    }

    return _SettingsGroup(
      title: 'Ports',
      icon: Icons.settings_ethernet,
      children: [
        row(report.gamePortUdp, 'Sessions you host'),
        row(report.gamePortTcp, 'Server info for LAN and direct joins'),
        row(report.lanPort, 'LAN discovery'),
        Text(
          '"Available" means only that the port could be opened just then, '
          'not that anyone can reach it.',
          style: Theme.of(context).textTheme.bodySmall,
        ),
      ],
    );
  }
}
