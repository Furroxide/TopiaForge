import 'package:launcher_domain/launcher_domain.dart';

/// These inputs bypass JSON construction, as public model callers may do.
ModManifest mutateConformanceModel(ModManifest parsed, String mutation) {
  final original = parsed.contributions!;
  final world = original.worlds.single;
  final mode = original.gamemodes.single;
  final target = original.launchTargets.single;
  final ModContributions changed;
  switch (mutation) {
    case 'empty-contributions':
      changed = const ModContributions();
    case 'missing-content':
    case 'missing-spawn':
      changed = ModContributions(
        worlds: [
          ModWorldDeclaration(
            id: world.id,
            name: world.name,
            content: mutation == 'missing-content' ? null : world.content,
            spawn: mutation == 'missing-spawn' ? null : world.spawn,
            transitions: world.transitions,
          ),
        ],
        gamemodes: original.gamemodes,
        launchTargets: original.launchTargets,
      );
    case 'missing-implementation':
    case 'empty-requirements':
      changed = ModContributions(
        worlds: original.worlds,
        gamemodes: [
          ModGamemodeDeclaration(
            id: mode.id,
            name: mode.name,
            implementation: mutation == 'missing-implementation'
                ? null
                : mode.implementation,
            worldRequirements: mutation == 'empty-requirements'
                ? const ModWorldRequirements()
                : null,
          ),
        ],
        launchTargets: original.launchTargets,
      );
    case 'missing-world':
      changed = ModContributions(
        worlds: original.worlds,
        gamemodes: original.gamemodes,
        launchTargets: [
          ModLaunchTargetDeclaration(
            id: target.id,
            title: target.title,
            gamemode: target.gamemode,
          ),
        ],
      );
    default:
      throw StateError('Unknown fixture model mutation $mutation');
  }
  return ModManifest(
    schemaVersion: parsed.schemaVersion,
    id: parsed.id,
    name: parsed.name,
    version: parsed.version,
    author: parsed.author,
    entryAssembly: parsed.entryAssembly,
    entryType: parsed.entryType,
    capabilities: parsed.capabilities,
    contributions: changed,
  );
}
