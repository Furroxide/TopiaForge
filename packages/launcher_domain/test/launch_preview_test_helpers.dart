import 'package:launcher_domain/launcher_domain.dart';

const ownerId = 'example.mod';
const targetId = '$ownerId.menu';
const worldId = '$ownerId.world';
const modeId = '$ownerId.mode';

ResolvedPackage previewPackage({
  String id = ownerId,
  List<ModWorldDeclaration>? worlds,
  List<ModGamemodeDeclaration>? modes,
  List<ModLaunchTargetDeclaration>? targets,
  List<ModDependency> dependencies = const [],
}) => ResolvedPackage(
  id: id,
  version: '1.0.0',
  manifest: ModManifest(
    schemaVersion: 6,
    id: id,
    name: id,
    version: '1.0.0',
    author: const ModAuthor(name: 'Test'),
    entryAssembly: 'Test.dll',
    entryType: 'Test.Mod',
    capabilities: const ['world-service'],
    dependencies: dependencies,
    contributions: ModContributions(
      worlds: worlds ?? [previewWorld()],
      gamemodes: modes ?? [previewMode()],
      launchTargets: targets ?? [previewTarget()],
    ),
  ),
);
ModWorldDeclaration previewWorld({
  String id = worldId,
  bool consent = true,
  List<String> transitions = ModTransitions.byPrecedence,
  String kind = 'game-scene',
  String? sceneName = 'UgcPlay',
}) => ModWorldDeclaration(
  id: id,
  name: id,
  content: ModWorldContent(kind: kind, sceneName: sceneName ?? ''),
  transitions: transitions,
  spawn: const ModSpawnPolicy(kind: 'provider-default'),
  openToAnyCompatible: consent,
);
ModGamemodeDeclaration previewMode({String id = modeId}) =>
    ModGamemodeDeclaration(
      id: id,
      name: id,
      implementation: const ModImplementationBinding(type: 'Test.Mode'),
    );
ModLaunchTargetDeclaration previewTarget({
  String id = targetId,
  String gamemode = modeId,
  String policy = 'open',
  bool? override = true,
  String transition = 'player-choice',
  String defaultWorld = worldId,
  List<String> allow = const [],
  int? order,
}) => ModLaunchTargetDeclaration(
  id: id,
  title: id,
  gamemode: gamemode,
  sortKey: order,
  world: ModWorldPolicy(
    policy: policy,
    defaultWorldId: defaultWorld,
    allowPlayerOverride: override,
    allow: allow,
  ),
  transition: transition,
);
EffectiveProfile previewProfile(
  List<ResolvedPackage> packages, {
  List<ResolvedPackage> disabled = const [],
  String id = 'profile',
  int revision = 1,
}) => EffectiveProfile(
  profileId: id,
  revision: revision,
  packages: packages,
  disabledPackages: disabled,
);
LaunchPreview buildPreview(
  EffectiveProfile profile, {
  LaunchSelection? selection,
  RuntimeObservation observation = RuntimeObservation.none,
  bool safe = false,
}) => LaunchPreviewBuilder.build(
  profile: profile,
  installIdentity: 'test-install',
  requestedSelection:
      selection ?? LaunchSelection.target(LaunchRequest(targetId: targetId)),
  observation: observation,
  safeMode: safe,
);
