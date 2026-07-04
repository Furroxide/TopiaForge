part of '../screens.dart';

class DiagnosticsScreen extends StatelessWidget {
  const DiagnosticsScreen({super.key, required this.state});

  final LauncherState state;

  @override
  Widget build(BuildContext context) {
    final install = state.gameInstall;
    if (install == null) {
      return _SetupFirstRunPanel(state: state);
    }

    return Column(
      children: [
        ScreenHeader(
          title: 'Diagnostics',
          subtitle:
              'Logs, runtime status, dependency graph, and repair actions.',
          trailing: Wrap(
            spacing: 8,
            children: [
              OutlinedButton.icon(
                onPressed: state.isBusy
                    ? null
                    : () => _add(context, const DiagnosticBundleRequested()),
                icon: const Icon(Icons.inventory_2),
                label: const Text('Create Bundle'),
              ),
              OutlinedButton.icon(
                onPressed: state.isBusy
                    ? null
                    : () => _add(context, const RuntimeRepaired()),
                icon: const Icon(Icons.build),
                label: const Text('Repair Runtime'),
              ),
              OutlinedButton.icon(
                onPressed: state.installedMods.isEmpty
                    ? null
                    : () => _add(context, const AllModsDisabled()),
                icon: const Icon(Icons.toggle_off),
                label: const Text('Disable All'),
              ),
            ],
          ),
        ),
        Expanded(
          child: Padding(
            padding: const EdgeInsets.fromLTRB(20, 0, 20, 20),
            child: Row(
              crossAxisAlignment: CrossAxisAlignment.stretch,
              children: [
                Expanded(flex: 2, child: _DiagnosticsSummary(state: state)),
                const SizedBox(width: 12),
                const Expanded(flex: 3, child: _LogsPane()),
              ],
            ),
          ),
        ),
      ],
    );
  }
}

class _DiagnosticsSummary extends StatelessWidget {
  const _DiagnosticsSummary({required this.state});

  final LauncherState state;

  @override
  Widget build(BuildContext context) {
    return Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        BorderedPane(
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              Text('Runtime', style: Theme.of(context).textTheme.titleMedium),
              const SizedBox(height: 8),
              Wrap(
                spacing: 8,
                runSpacing: 8,
                children: [
                  _componentPill('BepInEx', state.gameInstall!.bepInExStatus),
                  _componentPill('Loader', state.gameInstall!.loaderStatus),
                  if (state.gameInstall!.compatStatus != null)
                    _compatPill(state.gameInstall!.compatStatus!),
                ],
              ),
              if (state.diagnosticBundle != null) ...[
                const SizedBox(height: 10),
                _keyValue('Bundle', state.diagnosticBundle!.path),
              ],
              if (state.gameInstall!.compatStatus != null)
                _GameCompatSection(
                  compat: state.gameInstall!.compatStatus!,
                  busy: state.isBusy,
                ),
            ],
          ),
        ),
        const SizedBox(height: 12),
        Expanded(
          child: BorderedPane(
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Text(
                  'Dependency Graph',
                  style: Theme.of(context).textTheme.titleMedium,
                ),
                const SizedBox(height: 8),
                Expanded(
                  child: ListView(
                    children: [
                      ...state.resolution.issues.map(_issueTile),
                      if (state.resolution.issues.isEmpty)
                        Text(
                          'No dependency or conflict errors.',
                          style: Theme.of(context).textTheme.bodySmall,
                        ),
                      const Divider(),
                      ...state.resolution.graph.entries.map(
                        (entry) => _keyValue(
                          entry.key,
                          entry.value.isEmpty
                              ? 'no dependencies'
                              : entry.value.join(', '),
                        ),
                      ),
                    ],
                  ),
                ),
              ],
            ),
          ),
        ),
      ],
    );
  }
}

class _GameCompatSection extends StatelessWidget {
  const _GameCompatSection({required this.compat, required this.busy});

  final GameCompatStatus compat;
  final bool busy;

  @override
  Widget build(BuildContext context) {
    final findings = compat.findings;
    return Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        const SizedBox(height: 12),
        Row(
          children: [
            Expanded(
              child: Text(
                'Game Compatibility',
                style: Theme.of(context).textTheme.titleSmall,
              ),
            ),
            OutlinedButton.icon(
              onPressed: busy
                  ? null
                  : () => _add(context, const RecheckGameCompatRequested()),
              icon: const Icon(Icons.refresh, size: 16),
              label: const Text('Recheck'),
            ),
          ],
        ),
        const SizedBox(height: 4),
        Text(_headline, style: Theme.of(context).textTheme.bodySmall),
        if (findings.isNotEmpty) ...[
          const SizedBox(height: 6),
          ConstrainedBox(
            constraints: const BoxConstraints(maxHeight: 180),
            child: SingleChildScrollView(
              child: Column(
                crossAxisAlignment: CrossAxisAlignment.start,
                children: findings.map(_compatFindingTile).toList(),
              ),
            ),
          ),
        ],
      ],
    );
  }

  String get _headline {
    switch (compat.status) {
      case 'ok':
        final version = compat.gameVersionLabel.isNotEmpty
            ? ' (${compat.gameVersionLabel})'
            : '';
        return 'All mod features are compatible with the installed game$version.';
      case 'broken':
        return '${compat.errorCount} mod feature(s) rely on game APIs that changed in this '
            'version. Affected mods may partly stop working — the game still launches normally.';
      case 'skipped':
        return 'No game installation was detected to check.';
      default:
        return 'Compatibility could not be verified (the checker tool is unavailable).';
    }
  }
}

class _LogsPane extends StatelessWidget {
  const _LogsPane();

  @override
  Widget build(BuildContext context) {
    final state = context.watch<LauncherBloc>().state;
    return Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        Text('Logs', style: Theme.of(context).textTheme.titleMedium),
        const SizedBox(height: 8),
        Expanded(child: LogViewer(text: state.recentLog)),
      ],
    );
  }
}
