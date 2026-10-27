part of 'topiaforge.dart';

extension _MapSourceCommands on _TopiaForgeCli {
  Future<int> _worldSource(List<String> args) async {
    final repository = LocalLauncherRepository(
      dataRoot: _option(args, '--data-root'),
    );
    try {
      switch (args.firstOrNull) {
        case 'list':
          final sources = await repository.loadPublishedMapSources();
          if (sources.isEmpty) {
            stdout.writeln(
              'No saved maps. Use world source add <map-link-or-id> --scene <id> --name <name>.',
            );
          }
          for (final source in sources) {
            stdout.writeln('${source.name} · ${source.map} · ${source.scene}');
          }
        case 'add':
        case 'remove':
          if (args.length < 2 ||
              args[1].startsWith('--') ||
              _option(args, '--scene') == null) {
            throw UsageError(
              'Use world source ${args.first} <map-link-or-id> --scene <id> ${args.first == 'add' ? '--name <name>' : ''}.',
            );
          }
          final map = MapReference.parse(args[1]).id;
          final scene = _option(args, '--scene')!;
          if (args.first == 'add') {
            final name = _option(args, '--name');
            if (name == null) {
              throw UsageError(
                'Give this saved map a display name with --name.',
              );
            }
            await repository.savePublishedMapSource(
              PublishedMapSource.fromReference(
                reference: map,
                scene: scene,
                name: name,
              ),
            );
            stdout.writeln(
              'Saved map. It appears in the Free Play world picker after the next game start.',
            );
          } else {
            await repository.removePublishedMapSource(map, scene);
            stdout.writeln(
              'Removed the saved reference. Downloaded map bytes remain until Clear downloaded maps is used.',
            );
          }
        default:
          throw UsageError(
            'Usage: topiaforge world source list|add|remove [map-link-or-id] [--scene id] [--name name] [--data-root directory]',
          );
      }
      return 0;
    } finally {
      await repository.dispose();
    }
  }
}
