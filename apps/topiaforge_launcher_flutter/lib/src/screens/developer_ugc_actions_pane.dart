part of '../screens.dart';

extension _UgcLiveSyncActionsPane on _UgcLiveSyncPaneState {
  Widget _detachedActions(LauncherState state, bool busy) {
    return Wrap(
      spacing: 10,
      runSpacing: 10,
      children: [
        if (state.ugcPublisherRunning) _publisherButton(state, busy),
        _cleanupButton(busy),
      ],
    );
  }

  Widget _actions(LauncherState state, bool busy, bool hasInstall) {
    return Wrap(
      spacing: 10,
      runSpacing: 10,
      children: [
        FilledButton.icon(
          onPressed: busy || !hasInstall
              ? null
              : () => _add(context, const DeveloperUgcGoLive()),
          icon: const Icon(Icons.rocket_launch_outlined),
          label: const Text('Go Live'),
        ),
        OutlinedButton.icon(
          onPressed: busy ? null : _save,
          icon: const Icon(Icons.save_outlined),
          label: const Text('Save'),
        ),
        OutlinedButton.icon(
          onPressed: busy || !hasInstall
              ? null
              : () => _add(context, const DeveloperUgcConfigDeployed()),
          icon: const Icon(Icons.cloud_upload_outlined),
          label: const Text('Deploy to game'),
        ),
        OutlinedButton.icon(
          onPressed: busy
              ? null
              : () => _add(context, const DeveloperWatchFolderOpened()),
          icon: const Icon(Icons.folder_open),
          label: const Text('Open watch folder'),
        ),
        _cleanupButton(busy),
        _publisherButton(state, busy),
      ],
    );
  }

  Widget _cleanupButton(bool busy) {
    return OutlinedButton.icon(
      onPressed: busy
          ? null
          : () => _confirm(
              context,
              title: 'Clean up UGC live sync?',
              message:
                  'Stops the publisher, asks the running game to disconnect, clears captured live-document state, and disables auto-connect.',
              confirmLabel: 'Clean Up',
              action: () => _add(context, const DeveloperUgcCleanupRequested()),
            ),
      icon: const Icon(Icons.link_off),
      label: const Text('Clean Up Live Sync'),
    );
  }

  Widget _publisherButton(LauncherState state, bool busy) {
    return OutlinedButton.icon(
      onPressed: busy
          ? null
          : () => _add(context, const DeveloperUgcPublishToggled()),
      icon: Icon(
        state.ugcPublisherRunning ? Icons.stop_circle_outlined : Icons.podcasts,
      ),
      label: Text(
        state.ugcPublisherRunning
            ? 'Stop Automerge publisher'
            : 'Publish via Automerge',
      ),
    );
  }
}
