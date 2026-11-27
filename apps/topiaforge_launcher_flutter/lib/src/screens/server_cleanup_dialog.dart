part of '../screens.dart';

Future<void> _openServerCleanup(BuildContext context) async {
  final bloc = context.read<LauncherBloc>();
  bloc.add(const ServerCleanupRequested());
  await showDialog<void>(
    context: context,
    barrierDismissible: false,
    builder: (_) =>
        BlocProvider.value(value: bloc, child: const ServerCleanupDialog()),
  );
  if (!bloc.isClosed) bloc.add(const ServerCleanupClosed());
}

class ServerCleanupDialog extends StatefulWidget {
  const ServerCleanupDialog({super.key});
  @override
  State<ServerCleanupDialog> createState() => _ServerCleanupDialogState();
}

class _ServerCleanupDialogState extends State<ServerCleanupDialog> {
  final _selected = <String>{};
  @override
  Widget build(
    BuildContext context,
  ) => BlocBuilder<LauncherBloc, LauncherState>(
    builder: (context, state) {
      final view = state.multiplayer.cleanup;
      final catalog = view.catalog;
      final plan = view.plan;
      final applying = view.busy && view.reviewing;
      final profiles = catalog?.profiles ?? const <ServerContentProfile>[];
      final versions =
          (view.reviewing ? plan : catalog)?.versions ??
          const <ServerContentVersion>[];
      final items = <Widget>[
        const Text(
          'Choose whole servers to forget. Your own installed mods stay. Versions still used by your profiles or another kept server stay too.',
        ),
        const SizedBox(height: 10),
        const Text(
          'Shared download archives are retained because another install or host may still need them.',
        ),
        if (view.busy) const LinearProgressIndicator(),
        if (view.message.isNotEmpty) SessionNotice(message: view.message),
        if (catalog != null && profiles.isEmpty && versions.isEmpty)
          const Text('No server content is stored for this install.'),
        if (!view.reviewing) ...[
          for (final profile in profiles)
            CheckboxListTile(
              contentPadding: EdgeInsets.zero,
              title: Text(withoutAddresses(profile.name)),
              value: _selected.contains(profile.id),
              onChanged: view.busy
                  ? null
                  : (value) => setState(() {
                      if (value == true) {
                        _selected.add(profile.id);
                      } else {
                        _selected.remove(profile.id);
                      }
                    }),
            ),
        ] else if (plan != null) ...[
          Text(
            'Remove ${plan.profiles.length} server profiles and ${plan.removable.length} installed versions (${_cleanupBytes(plan.removableBytes)}).',
          ),
          for (final profile in plan.profiles)
            Text('Remove profile: ${withoutAddresses(profile.name)}'),
          const Text(
            'Joining one of these servers again may download its mods again. This removal cannot be undone.',
          ),
        ],
        const SizedBox(height: 12),
        for (final version in versions)
          ListTile(
            contentPadding: EdgeInsets.zero,
            title: Text(
              '${withoutAddresses(version.modId)} ${withoutAddresses(version.version)}',
            ),
            subtitle: Text(
              '${_cleanupBytes(version.sizeBytes)} · ${_keptReason(version, view.reviewing)}\n'
              'Used by: ${version.serverProfileIds.map((id) => _profileName(id, profiles)).join(', ')}',
            ),
            leading: Icon(
              view.reviewing && version.removable
                  ? Icons.delete_outline
                  : Icons.inventory_2_outlined,
            ),
          ),
      ];
      return PopScope(
        canPop: !applying,
        child: AlertDialog(
          key: const Key('server-cleanup-dialog'),
          title: Text(
            view.reviewing ? 'Review server content removal' : 'Server content',
          ),
          content: SizedBox(
            width: 680,
            height: MediaQuery.sizeOf(context).height * .6,
            child: ListView.builder(
              itemCount: items.length,
              itemBuilder: (_, index) => items[index],
            ),
          ),
          actions: [
            TextButton(
              autofocus: true,
              onPressed: applying ? null : () => Navigator.pop(context),
              child: const Text('Close'),
            ),
            OutlinedButton(
              onPressed: view.busy
                  ? null
                  : () => _add(context, const ServerCleanupRequested()),
              child: Text(view.reviewing ? 'Back' : 'Refresh'),
            ),
            if (!view.reviewing &&
                catalog != null &&
                (profiles.isNotEmpty || versions.isNotEmpty))
              FilledButton(
                onPressed: view.busy
                    ? null
                    : () => _add(
                        context,
                        ServerCleanupRequested(
                          profiles: Set.unmodifiable(_selected),
                        ),
                      ),
                child: const Text('Review removal'),
              ),
            if (view.reviewing && plan != null)
              FilledButton.icon(
                key: const Key('server-cleanup-confirm'),
                style: FilledButton.styleFrom(
                  backgroundColor: Theme.of(context).colorScheme.error,
                ),
                onPressed: view.busy || plan.isEmpty
                    ? null
                    : () => _add(
                        context,
                        ServerCleanupConfirmed(plan.fingerprint),
                      ),
                icon: const Icon(Icons.delete_forever),
                label: const Text('Remove reviewed content'),
              ),
          ],
        ),
      );
    },
  );

  String _profileName(String id, List<ServerContentProfile> profiles) {
    for (final profile in profiles) {
      if (profile.id == id) return withoutAddresses(profile.name);
    }
    return 'Another server';
  }

  String _keptReason(ServerContentVersion version, bool reviewing) =>
      switch (version.kept) {
        ServerContentKept.usedByProfile => 'Kept: used by your profile',
        ServerContentKept.pinnedByKeptServer => 'Kept: another server needs it',
        null =>
          reviewing ? 'Will be removed' : 'May be removed with its servers',
      };
}

String _cleanupBytes(int bytes) => bytes < 1024
    ? '$bytes B'
    : bytes < 1024 * 1024
    ? '${(bytes / 1024).toStringAsFixed(1)} KiB'
    : '${(bytes / (1024 * 1024)).toStringAsFixed(1)} MiB';
