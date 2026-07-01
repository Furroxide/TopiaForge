part of '../screens.dart';

Future<void> _showAddSourceDialog(BuildContext context) async {
  final nameController = TextEditingController();
  final urlController = TextEditingController();
  final result = await showDialog<(String, String)>(
    context: context,
    builder: (context) => AlertDialog(
      title: const Text('Add package source'),
      content: SizedBox(
        width: 520,
        child: Column(
          mainAxisSize: MainAxisSize.min,
          children: [
            TextField(
              controller: nameController,
              decoration: const InputDecoration(labelText: 'Name'),
              autofocus: true,
            ),
            const SizedBox(height: 10),
            TextField(
              controller: urlController,
              decoration: const InputDecoration(labelText: 'Source URL'),
            ),
          ],
        ),
      ),
      actions: [
        TextButton(
          onPressed: () => Navigator.of(context).pop(),
          child: const Text('Cancel'),
        ),
        FilledButton(
          onPressed: () {
            final name = nameController.text.trim();
            final url = urlController.text.trim();
            if (name.isEmpty || url.isEmpty) {
              return;
            }
            Navigator.of(context).pop((name, url));
          },
          child: const Text('Add'),
        ),
      ],
    ),
  );
  nameController.dispose();
  urlController.dispose();
  if (result != null && context.mounted) {
    _add(context, PackageSourceAdded(name: result.$1, url: result.$2));
  }
}

Future<void> _showNewModDialog(BuildContext context) async {
  final idController = TextEditingController();
  final nameController = TextEditingController();
  var includeUnityCompanion = false;
  final result = await showDialog<({String id, String name, bool unity})>(
    context: context,
    builder: (context) => StatefulBuilder(
      builder: (context, setState) => AlertDialog(
        title: const Text('New mod project'),
        content: SizedBox(
          width: 520,
          child: Column(
            mainAxisSize: MainAxisSize.min,
            children: [
              TextField(
                controller: idController,
                decoration: const InputDecoration(
                  labelText: 'Mod id',
                  hintText: 'author.mymod',
                ),
                autofocus: true,
              ),
              const SizedBox(height: 10),
              TextField(
                controller: nameController,
                decoration: const InputDecoration(labelText: 'Display name'),
              ),
              const SizedBox(height: 4),
              CheckboxListTile(
                contentPadding: EdgeInsets.zero,
                value: includeUnityCompanion,
                onChanged: (value) =>
                    setState(() => includeUnityCompanion = value ?? false),
                title: const Text('Include Unity companion (UGC live-sync)'),
              ),
            ],
          ),
        ),
        actions: [
          TextButton(
            onPressed: () => Navigator.of(context).pop(),
            child: const Text('Cancel'),
          ),
          FilledButton(
            onPressed: () {
              final id = idController.text.trim();
              final name = nameController.text.trim();
              if (id.isEmpty) {
                return;
              }
              Navigator.of(context).pop((
                id: id,
                name: name.isEmpty ? id : name,
                unity: includeUnityCompanion,
              ));
            },
            child: const Text('Create'),
          ),
        ],
      ),
    ),
  );
  idController.dispose();
  nameController.dispose();
  if (result != null && context.mounted) {
    _add(
      context,
      DeveloperModProjectCreated(
        id: result.id,
        name: result.name,
        includeUnityCompanion: result.unity,
      ),
    );
  }
}

Future<void> _showAddVpmRepoDialog(BuildContext context) async {
  final urlController = TextEditingController();
  final result = await showDialog<String>(
    context: context,
    builder: (context) => AlertDialog(
      title: const Text('Add VPM repository'),
      content: SizedBox(
        width: 560,
        child: TextField(
          controller: urlController,
          decoration: const InputDecoration(
            labelText: 'Listing index.json (path or https url)',
            hintText: r'C:\…\dist\vpm\index.json or https://host/index.json',
          ),
          autofocus: true,
        ),
      ),
      actions: [
        TextButton(
          onPressed: () => Navigator.of(context).pop(),
          child: const Text('Cancel'),
        ),
        FilledButton(
          onPressed: () {
            final url = urlController.text.trim();
            if (url.isEmpty) {
              return;
            }
            Navigator.of(context).pop(url);
          },
          child: const Text('Add'),
        ),
      ],
    ),
  );

  urlController.dispose();
  if (result != null && context.mounted) {
    _add(context, DeveloperUnityRepoAdded(result));
  }
}

Future<void> _showNewUnityProjectDialog(BuildContext context) async {
  final nameController = TextEditingController();
  final result = await showDialog<String>(
    context: context,
    builder: (context) => AlertDialog(
      title: const Text('New Unity world project'),
      content: SizedBox(
        width: 520,
        child: Column(
          mainAxisSize: MainAxisSize.min,
          children: [
            TextField(
              controller: nameController,
              decoration: const InputDecoration(
                labelText: 'Project name',
                hintText: 'My Robotopia World',
              ),
              autofocus: true,
            ),
            const SizedBox(height: 8),
            const Text(
              'Copies the Unity world template and installs the UGC companion. '
              'Open it in Unity to author markers, then Go Live from the cockpit.',
              style: TextStyle(fontSize: 12),
            ),
          ],
        ),
      ),
      actions: [
        TextButton(
          onPressed: () => Navigator.of(context).pop(),
          child: const Text('Cancel'),
        ),
        FilledButton(
          onPressed: () {
            final name = nameController.text.trim();
            if (name.isEmpty) {
              return;
            }
            Navigator.of(context).pop(name);
          },
          child: const Text('Create'),
        ),
      ],
    ),
  );

  nameController.dispose();
  if (result != null && context.mounted) {
    _add(context, DeveloperUnityProjectCreated(name: result));
  }
}

Future<void> _showAddExistingProjectDialog(BuildContext context) async {
  final pathController = TextEditingController();
  final result = await showDialog<String>(
    context: context,
    builder: (context) => AlertDialog(
      title: const Text('Add existing project'),
      content: SizedBox(
        width: 560,
        child: Row(
          crossAxisAlignment: CrossAxisAlignment.end,
          children: [
            Expanded(
              child: TextField(
                controller: pathController,
                decoration: const InputDecoration(
                  labelText: 'Project folder',
                  hintText:
                      'A folder with robotopia.project.json, Packages/vpm-manifest.json, or package.json',
                ),
                autofocus: true,
              ),
            ),
            const SizedBox(width: 8),
            OutlinedButton(
              onPressed: () async {
                final picked = await getDirectoryPath(
                  confirmButtonText: 'Select project folder',
                );
                if (picked != null) {
                  pathController.text = picked;
                }
              },
              child: const Text('Browse'),
            ),
          ],
        ),
      ),
      actions: [
        TextButton(
          onPressed: () => Navigator.of(context).pop(),
          child: const Text('Cancel'),
        ),
        FilledButton(
          onPressed: () {
            final path = pathController.text.trim();
            if (path.isEmpty) {
              return;
            }
            Navigator.of(context).pop(path);
          },
          child: const Text('Add'),
        ),
      ],
    ),
  );

  pathController.dispose();
  if (result != null && context.mounted) {
    _add(context, DeveloperProjectAdded(result));
  }
}
