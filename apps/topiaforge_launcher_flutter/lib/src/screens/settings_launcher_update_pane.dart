part of '../screens.dart';

// The Settings screen's signed launcher-update pane: update settings, check
// and download progress, and the confirmed restart-and-install step. Split
// out of `settings_screen.dart` to stay inside the 500-line cap in AGENTS.md.

class _LauncherUpdateSettings extends StatelessWidget {
  const _LauncherUpdateSettings({required this.state});

  final LauncherState state;

  @override
  Widget build(BuildContext context) {
    final settings = state.launcherUpdates;
    final status = state.launcherUpdateStatus;
    final checking =
        status.phase == LauncherUpdatePhase.checking ||
        status.phase == LauncherUpdatePhase.downloading;
    return BorderedPane(
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Row(
            children: [
              Expanded(
                child: Text(
                  'Launcher Updates',
                  style: Theme.of(context).textTheme.titleMedium,
                ),
              ),
              StatusPill(
                label: _phaseLabel(status.phase),
                tone: _phaseTone(status.phase),
              ),
            ],
          ),
          const SizedBox(height: 8),
          Text(
            'TopiaForge ${TopiaForgeLauncherBuild.version} verifies '
            'Ed25519-signed release metadata before downloading a complete '
            'platform package. Installing always requires confirmation.',
          ),
          const SizedBox(height: 8),
          SwitchListTile(
            contentPadding: EdgeInsets.zero,
            value: settings.enabled,
            onChanged: (enabled) =>
                _add(context, LauncherUpdateSettingsChanged(enabled: enabled)),
            title: const Text('Enable signed update checks'),
            subtitle: const Text(
              'Prerelease builds check the beta channel by default.',
            ),
          ),
          SwitchListTile(
            contentPadding: EdgeInsets.zero,
            value: settings.checkAutomatically,
            onChanged: settings.enabled
                ? (enabled) => _add(
                    context,
                    LauncherUpdateSettingsChanged(checkAutomatically: enabled),
                  )
                : null,
            title: const Text('Check at startup'),
            subtitle: const Text('A persisted cooldown limits network checks.'),
          ),
          const SizedBox(height: 8),
          DropdownButtonFormField<LauncherUpdateChannel>(
            initialValue: settings.channel,
            // Fills the pane, so a long name at large text shortens instead
            // of overflowing.
            isExpanded: true,
            decoration: const InputDecoration(labelText: 'Update channel'),
            items: const [
              DropdownMenuItem(
                value: LauncherUpdateChannel.release,
                child: Text('Stable', overflow: TextOverflow.ellipsis),
              ),
              DropdownMenuItem(
                value: LauncherUpdateChannel.beta,
                child: Text(
                  'Beta (includes stable)',
                  overflow: TextOverflow.ellipsis,
                ),
              ),
            ],
            onChanged: settings.enabled
                ? (channel) {
                    if (channel != null) {
                      _add(
                        context,
                        LauncherUpdateSettingsChanged(channel: channel),
                      );
                    }
                  }
                : null,
          ),
          const SizedBox(height: 12),
          if (checking) ...[
            LinearProgressIndicator(
              value: status.phase == LauncherUpdatePhase.downloading
                  ? status.progress.clamp(0, 1)
                  : null,
            ),
            const SizedBox(height: 8),
          ],
          if (status.message.isNotEmpty)
            SelectableText(status.message)
          else
            const Text('No update check has run in this session.'),
          if (status.candidate case final candidate?) ...[
            const SizedBox(height: 8),
            SelectableText(
              'Candidate ${candidate.version} • ${candidate.signingKeyId}\n'
              'Payload SHA-256 ${candidate.payloadSha256}',
            ),
          ],
          const SizedBox(height: 12),
          Wrap(
            spacing: 8,
            runSpacing: 8,
            children: [
              OutlinedButton.icon(
                onPressed: settings.enabled && !checking
                    ? () => _add(context, const LauncherUpdateCheckRequested())
                    : null,
                icon: const Icon(Icons.refresh),
                label: const Text('Check now'),
              ),
              if (status.phase == LauncherUpdatePhase.available)
                FilledButton.icon(
                  onPressed: () =>
                      _add(context, const LauncherUpdateDownloadRequested()),
                  icon: const Icon(Icons.download),
                  label: const Text('Download and verify'),
                ),
              if (status.phase == LauncherUpdatePhase.staged)
                FilledButton.icon(
                  onPressed: () => _confirmInstall(context),
                  icon: const Icon(Icons.restart_alt),
                  label: const Text('Restart and install'),
                ),
            ],
          ),
          const SizedBox(height: 8),
          const SelectableText(
            'Recovery and manual downloads: '
            'https://github.com/furroxide/TopiaForge/releases',
          ),
        ],
      ),
    );
  }

  Future<void> _confirmInstall(BuildContext context) async {
    final confirmed = await showDialog<bool>(
      context: context,
      builder: (dialogContext) => AlertDialog(
        title: const Text('Install verified update?'),
        content: const Text(
          'TopiaForge will close, replace the complete portable package, and '
          'roll back automatically if the updated launcher does not report a '
          'healthy startup.',
        ),
        actions: [
          TextButton(
            onPressed: () => Navigator.pop(dialogContext, false),
            child: const Text('Cancel'),
          ),
          FilledButton(
            onPressed: () => Navigator.pop(dialogContext, true),
            child: const Text('Restart and install'),
          ),
        ],
      ),
    );
    if (confirmed == true && context.mounted) {
      _add(context, const LauncherUpdateInstallConfirmed());
    }
  }
}

String _phaseLabel(LauncherUpdatePhase phase) => switch (phase) {
  LauncherUpdatePhase.idle => 'Idle',
  LauncherUpdatePhase.checking => 'Checking',
  LauncherUpdatePhase.current => 'Current',
  LauncherUpdatePhase.available => 'Available',
  LauncherUpdatePhase.downloading => 'Downloading',
  LauncherUpdatePhase.staged => 'Verified',
  LauncherUpdatePhase.applying => 'Restarting',
  LauncherUpdatePhase.recovering => 'Recovering',
  LauncherUpdatePhase.failed => 'Attention',
};

StatusTone _phaseTone(LauncherUpdatePhase phase) => switch (phase) {
  LauncherUpdatePhase.failed => StatusTone.danger,
  LauncherUpdatePhase.available ||
  LauncherUpdatePhase.downloading ||
  LauncherUpdatePhase.staged => StatusTone.warning,
  LauncherUpdatePhase.current => StatusTone.good,
  _ => StatusTone.neutral,
};
