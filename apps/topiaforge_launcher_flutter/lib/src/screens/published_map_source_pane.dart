part of '../screens.dart';

class _PublishedMapSourcePane extends StatelessWidget {
  const _PublishedMapSourcePane({required this.state});
  final LauncherState state;
  @override
  Widget build(BuildContext context) => BorderedPane(
    child: Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        Wrap(
          spacing: 12,
          runSpacing: 8,
          crossAxisAlignment: WrapCrossAlignment.center,
          children: [
            Text(
              'Saved published maps',
              style: Theme.of(context).textTheme.titleMedium,
            ),
            OutlinedButton.icon(
              onPressed: state.isBusy ? null : () => _save(context),
              icon: const Icon(Icons.add_link),
              label: const Text('Add map'),
            ),
          ],
        ),
        const Text(
          'Save a public robotopia.gg map and scene. It appears in Free Play after the next game start with TopiaForge Worlds enabled. Maps download when played; a saved copy works offline.',
        ),
        if (state.publishedMapSources.isEmpty)
          const Padding(
            padding: EdgeInsets.symmetric(vertical: 12),
            child: Text(
              'No maps saved yet. Add a map link or id and choose its scene id.',
            ),
          ),
        if (state.publishedMapSources.isNotEmpty)
          SizedBox(
            height: 240,
            child: ListView.builder(
              itemCount: state.publishedMapSources.length,
              itemBuilder: (context, index) {
                final source = state.publishedMapSources[index];
                return ListTile(
                  title: Text(
                    source.name,
                    maxLines: 2,
                    overflow: TextOverflow.ellipsis,
                  ),
                  subtitle: Text(
                    '${source.map} · ${source.scene}',
                    maxLines: 2,
                    overflow: TextOverflow.ellipsis,
                  ),
                  trailing: IconButton(
                    tooltip: 'Remove saved map',
                    icon: const Icon(Icons.link_off),
                    onPressed: state.isBusy
                        ? null
                        : () => _remove(context, source),
                  ),
                );
              },
            ),
          ),
      ],
    ),
  );
  Future<void> _save(BuildContext context) async {
    final source = await showDialog<PublishedMapSource>(
      context: context,
      builder: (_) => const _PublishedMapDialog(),
    );
    if (source != null && context.mounted) {
      _add(context, PublishedMapSourceSaved(source));
    }
  }

  Future<void> _remove(BuildContext context, PublishedMapSource source) async {
    final confirmed = await showDialog<bool>(
      context: context,
      builder: (dialog) => AlertDialog(
        title: const Text('Remove saved map?'),
        content: Text(
          'Remove the reference to ${source.name}? The published map and downloaded copies are unchanged.',
        ),
        actions: [
          TextButton(
            onPressed: () => Navigator.pop(dialog, false),
            child: const Text('Cancel'),
          ),
          FilledButton(
            onPressed: () => Navigator.pop(dialog, true),
            child: const Text('Remove'),
          ),
        ],
      ),
    );
    if (confirmed == true && context.mounted) {
      _add(context, PublishedMapSourceRemoved(source.map, source.scene));
    }
  }
}

class _PublishedMapDialog extends StatefulWidget {
  const _PublishedMapDialog();
  @override
  State<_PublishedMapDialog> createState() => _PublishedMapDialogState();
}

class _PublishedMapDialogState extends State<_PublishedMapDialog> {
  final name = TextEditingController(),
      map = TextEditingController(),
      scene = TextEditingController();
  String? error;
  @override
  void dispose() {
    name.dispose();
    map.dispose();
    scene.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) => AlertDialog(
    title: const Text('Save a published map'),
    content: SizedBox(
      width: 440,
      child: SingleChildScrollView(
        child: Column(
          mainAxisSize: MainAxisSize.min,
          children: [
            TextField(
              controller: name,
              autofocus: true,
              maxLength: 128,
              decoration: const InputDecoration(labelText: 'Display name'),
            ),
            TextField(
              controller: map,
              maxLength: 256,
              decoration: const InputDecoration(labelText: 'Map link or id'),
            ),
            TextField(
              controller: scene,
              maxLength: 128,
              decoration: const InputDecoration(
                labelText: 'Scene id from the map editor',
              ),
            ),
            if (error != null)
              Text(
                error!,
                style: TextStyle(color: Theme.of(context).colorScheme.error),
              ),
          ],
        ),
      ),
    ),
    actions: [
      TextButton(
        onPressed: () => Navigator.pop(context),
        child: const Text('Cancel'),
      ),
      FilledButton(
        onPressed: () {
          try {
            Navigator.pop(
              context,
              PublishedMapSource.fromReference(
                reference: map.text,
                scene: scene.text,
                name: name.text,
              ),
            );
          } on FormatException catch (failure) {
            setState(() => error = failure.message);
          }
        },
        child: const Text('Save map'),
      ),
    ],
  );
}
