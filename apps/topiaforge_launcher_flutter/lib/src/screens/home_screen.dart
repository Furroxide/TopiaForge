part of '../screens.dart';

/// The launcher's landing screen: a mission-control style launch pad with the
/// primary Launch action, a friendly systems check, quick profile switching,
/// and a discovery rail into the mod registry. Detailed launch configuration
/// (runtime repair, world selection, load order) lives on the Setup screen.
class HomeScreen extends StatelessWidget {
  const HomeScreen({super.key, required this.state});

  final LauncherState state;

  @override
  Widget build(BuildContext context) {
    if (state.gameInstall == null) {
      return _HomeFirstRun(state: state);
    }
    return SingleChildScrollView(
      // Top padding leaves room for the mascot overhanging the hero pane.
      padding: const EdgeInsets.fromLTRB(24, 44, 24, 24),
      child: Center(
        child: ConstrainedBox(
          constraints: const BoxConstraints(maxWidth: 1180),
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              StaggeredReveal(index: 0, child: _HeroLaunchPane(state: state)),
              const SizedBox(height: 24),
              StaggeredReveal(index: 1, child: _JumpBackInRow(state: state)),
              const SizedBox(height: 24),
              StaggeredReveal(index: 2, child: _DiscoverRail(state: state)),
            ],
          ),
        ),
      ),
    );
  }
}

String _worldNameFor(LauncherState state, WorldSelection selection) {
  final worlds = state.worldCatalog.worlds;
  for (final world in worlds) {
    if (world.id == selection.worldId) {
      return world.name;
    }
  }
  return worlds.isEmpty ? 'Default world' : worlds.first.name;
}

int _updatesAvailable(LauncherState state) {
  return state.registryMods.where((mod) => mod.updateAvailable).length;
}
