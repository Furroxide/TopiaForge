part of '../screens.dart';

/// Developer-toolchain health pane: surfaces `checkEnvironment()` (the same audit as `robotopia doctor`) with
/// per-tool status, remediation links, and a one-click Setup/auto-fix (`runSetup()`), all inside the Dev tab.
class _EnvironmentPane extends StatelessWidget {
  const _EnvironmentPane({required this.state});

  final LauncherState state;

  @override
  Widget build(BuildContext context) {
    final env = state.developerEnvironment;
    final setup = state.developerSetup;
    final busy = state.isBusy;

    return BorderedPane(
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Row(
            children: [
              Expanded(
                child: Text(
                  'Environment',
                  style: Theme.of(context).textTheme.titleMedium,
                ),
              ),
              if (env != null)
                StatusPill(
                  label: env.developerReady
                      ? 'Toolchain ready'
                      : 'Action needed',
                  tone: env.developerReady
                      ? StatusTone.good
                      : StatusTone.warning,
                  icon: env.developerReady ? Icons.check_circle : Icons.warning,
                ),
            ],
          ),
          const SizedBox(height: 10),
          if (env == null)
            const Text('Checking your developer toolchain…')
          else ...[
            const Text(
              'Consuming mods needs no developer tools. These checks are for building mods.',
              style: TextStyle(
                color: QuantumWorksPalette.mutedText,
                fontSize: 12,
              ),
            ),
            const SizedBox(height: 8),
            ..._group(
              context,
              'Build mods (.NET)',
              env.ofPurpose(ToolPurpose.develop),
            ),
            ..._group(context, 'UGC live-sync (optional)', [
              ...env.ofPurpose(ToolPurpose.ugcUnity),
              ...env.ofPurpose(ToolPurpose.ugcAutomerge),
            ]),
            ..._group(context, 'Other', env.ofPurpose(ToolPurpose.optional)),
          ],
          if (setup != null && setup.actions.isNotEmpty) ...[
            const SizedBox(height: 10),
            const Divider(),
            Text('Setup log', style: Theme.of(context).textTheme.labelLarge),
            const SizedBox(height: 4),
            ...setup.actions.map(
              (action) => Text(
                '- $action',
                style: const TextStyle(
                  color: QuantumWorksPalette.mutedText,
                  fontSize: 12,
                ),
              ),
            ),
          ],
          const SizedBox(height: 12),
          Wrap(
            spacing: 10,
            runSpacing: 10,
            children: [
              OutlinedButton.icon(
                onPressed: busy
                    ? null
                    : () => _add(context, const DeveloperEnvironmentChecked()),
                icon: const Icon(Icons.refresh),
                label: const Text('Re-check'),
              ),
              FilledButton.icon(
                onPressed: busy
                    ? null
                    : () => _add(context, const DeveloperSetupRequested()),
                icon: const Icon(Icons.healing_outlined),
                label: const Text('Setup / Auto-fix'),
              ),
            ],
          ),
        ],
      ),
    );
  }

  List<Widget> _group(
    BuildContext context,
    String title,
    Iterable<ToolCheck> checks,
  ) {
    final list = checks.toList();
    if (list.isEmpty) {
      return const [];
    }
    return [
      Padding(
        padding: const EdgeInsets.only(top: 8, bottom: 2),
        child: Text(title, style: Theme.of(context).textTheme.labelLarge),
      ),
      ...list.map((check) => _ToolCheckRow(check: check)),
    ];
  }
}

class _ToolCheckRow extends StatelessWidget {
  const _ToolCheckRow({required this.check});

  final ToolCheck check;

  @override
  Widget build(BuildContext context) {
    final (icon, color) = switch (check.status) {
      ToolStatus.ok => (Icons.check_circle, QuantumWorksPalette.good),
      ToolStatus.outdated => (Icons.update, QuantumWorksPalette.warning),
      ToolStatus.warning => (Icons.info_outline, QuantumWorksPalette.warning),
      ToolStatus.missing => (Icons.cancel, QuantumWorksPalette.danger),
    };
    final label = check.detail.isEmpty
        ? check.name
        : '${check.name} — ${check.detail}';

    return Padding(
      padding: const EdgeInsets.only(bottom: 6),
      child: Row(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Icon(icon, size: 18, color: color),
          const SizedBox(width: 8),
          Expanded(
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Text(label, style: const TextStyle(fontSize: 13)),
                if (!check.ok && check.remediation.isNotEmpty)
                  Text(
                    check.remediation,
                    style: const TextStyle(
                      color: QuantumWorksPalette.mutedText,
                      fontSize: 12,
                    ),
                  ),
              ],
            ),
          ),
          if (!check.ok && check.url.isNotEmpty)
            TextButton(
              onPressed: () =>
                  _add(context, DeveloperToolLinkOpened(check.url)),
              child: const Text('Install…'),
            ),
        ],
      ),
    );
  }
}
