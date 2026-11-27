part of '../screens.dart';

class _DownloadedMapSettings extends StatelessWidget {
  const _DownloadedMapSettings({required this.state});
  final LauncherState state;
  @override
  Widget build(BuildContext context) => BorderedPane(
    child: Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        Text('Downloaded maps', style: Theme.of(context).textTheme.titleMedium),
        const SizedBox(height: 8),
        const Text(
          'Published maps are saved for offline play. Clear them while Robotopia is closed to free disk space.',
        ),
        const SizedBox(height: 12),
        OutlinedButton.icon(
          key: const Key('clear-downloaded-maps'),
          onPressed: state.isBusy || state.gameInstall == null
              ? null
              : () => _confirm(
                  context,
                  title: 'Clear downloaded maps?',
                  message:
                      'These maps will need an internet connection to download again the next time you play.',
                  confirmLabel: 'Clear maps',
                  action: () =>
                      _add(context, const DownloadedMapsClearRequested()),
                ),
          icon: const Icon(Icons.delete_outline),
          label: const Text('Clear downloaded maps'),
        ),
      ],
    ),
  );
}
