part of 'widget_test.dart';

LaunchPreview _buildFakePreview(
  LauncherSnapshot snapshot,
  GameInstall install,
  LauncherProfile profile,
  LaunchSelection? override,
) {
  final selected = profile.launchSettings.safeMode
      ? <InstalledMod>[]
      : snapshot.installedMods
            .where(
              (mod) =>
                  !mod.uninstallPending &&
                  (profile.inheritManagerModState
                      ? mod.enabled
                      : profile.enabledMods.contains(mod.id)),
            )
            .toList();
  final disabled = snapshot.installedMods.where(
    (mod) => !selected.contains(mod),
  );
  ResolvedPackage package(InstalledMod mod) => ResolvedPackage(
    id: mod.id,
    version: mod.version,
    manifest: mod.manifest!,
  );
  final effective = EffectiveProfile(
    profileId: profile.id,
    revision: profile.revision,
    packages: selected.where((mod) => mod.manifest != null).map(package),
    disabledPackages: disabled
        .where((mod) => mod.manifest != null)
        .map(package),
  );
  return LaunchPreviewBuilder.build(
    profile: effective,
    installIdentity: install.path,
    requestedSelection: override ?? profile.launchSelection,
    safeMode: profile.launchSettings.safeMode,
    issues: const DependencyPlanner()
        .resolveInstalled(
          selected,
          gameVersion: install.gameVersion,
          requireKnownGameVersion: true,
          platform: 'windows',
          contentTargets: const ['code', 'standalonewindows64'],
        )
        .issues,
  );
}

LauncherSnapshot _replaceSnapshotProfiles(
  LauncherSnapshot snapshot,
  List<LauncherProfile> profiles,
  String selectedId,
) => LauncherSnapshot(
  gameInstall: snapshot.gameInstall,
  gameInstallCandidates: snapshot.gameInstallCandidates,
  profiles: profiles,
  selectedProfileId: selectedId,
  installedMods: snapshot.installedMods,
  registryMods: snapshot.registryMods,
  packageSources: snapshot.packageSources,
  worldCatalog: snapshot.worldCatalog,
  recentLog: snapshot.recentLog,
  launcherUpdates: snapshot.launcherUpdates,
  developerMode: snapshot.developerMode,
  sourceStatuses: snapshot.sourceStatuses,
  launcherLog: snapshot.launcherLog,
);

const _targetPackage = 'io.github.furroxide.topiaforge.zombies';
const _targetId = '$_targetPackage.menu';

List<InstalledMod> _declaredLaunchMods({bool overrides = false}) {
  final manifest = ModManifest(
    schemaVersion: 6,
    id: _targetPackage,
    name: 'Zombies',
    version: '1.0.0',
    author: const ModAuthor(name: 'Test'),
    entryAssembly: 'Fixture.dll',
    entryType: 'Fixture.Entry',
    contributions: ModContributions(
      worlds: [
        for (final name in ['arena', if (overrides) 'alternate'])
          ModWorldDeclaration(
            id: '$_targetPackage.$name',
            name: name == 'arena' ? 'Arena' : 'Alternate',
            content: const ModWorldContent(
              kind: 'game-scene',
              sceneName: 'FixtureScene',
            ),
            transitions: const [
              ModTransitions.additiveArena,
              ModTransitions.sceneReplacement,
            ],
            spawn: const ModSpawnPolicy(kind: 'provider-default'),
          ),
      ],
      gamemodes: const [
        ModGamemodeDeclaration(
          id: '$_targetPackage.survival',
          name: 'Survival',
          implementation: ModImplementationBinding(type: 'Fixture.Gamemode'),
          sceneChangePolicy: 'end-session',
        ),
      ],
      launchTargets: [
        ModLaunchTargetDeclaration(
          id: _targetId,
          title: 'Zombies',
          gamemode: '$_targetPackage.survival',
          world: ModWorldPolicy(
            policy: overrides ? 'list' : 'fixed',
            defaultWorldId: '$_targetPackage.arena',
            allow: overrides ? const ['$_targetPackage.alternate'] : const [],
            allowPlayerOverride: overrides ? true : null,
          ),
          transition: overrides ? 'player-choice' : 'auto',
        ),
      ],
    ),
  );
  return [_installedMod(manifest, enabled: true)];
}
