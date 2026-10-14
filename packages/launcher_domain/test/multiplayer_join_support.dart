import 'dart:convert';

import 'package:launcher_domain/launcher_domain.dart';

import 'multiplayer_fixtures.dart';

/// Host A's keys from `tests/fixtures/multiplayer/descriptor/cases.json`.
const hostASigningKey = 'X_63eOUQF6uSrrIZ1DhX5w-ouzCDzlMBavIFLxvmABE';
const hostATransportKey = 'RIcUrvBv2CmtHVUuyjEvJ7SN1V5vghS4UWaimHb8Y2s';
const hostAServerId =
    'c5f39060271ebd2f22d9bf155a26bf459274e31b795145fd9d862dc206d98bb3';

final testEnvironment = JoinEnvironment(
  gameBuild: '2545',
  runtimeVersion: '0.2.0',
  sdkVersion: '0.2.0',
  protocolVersion: '1.0.0',
  platform: 'windows',
  architecture: 'x64',
);

JoinEnvironment environmentWith({
  String gameBuild = '2545',
  String runtimeVersion = '0.2.0',
  String protocolVersion = '1.0.0',
  bool developerMode = false,
  String? platform = 'windows',
}) => JoinEnvironment(
  gameBuild: gameBuild,
  runtimeVersion: runtimeVersion,
  sdkVersion: runtimeVersion,
  protocolVersion: protocolVersion,
  platform: platform,
  architecture: 'x64',
  developerMode: developerMode,
);

/// The exact-archive digest a test package uses.
String testSha(String id, [String version = '1.0.0']) =>
    PureSha256.hex(utf8.encode('$id@$version'));

/// A V6 manifest. [mode] is null for an implicitly shared mod. A session
/// manifest without its contract [lock] is invalid for admission.
ModManifest testManifest(
  String id, {
  String version = '1.0.0',
  String? mode,
  String presence = 'required',
  List<String> capabilities = const [],
  String loaderRange = '*',
  String sdkRange = '*',
  List<String> platforms = const [],
  bool withLock = true,
}) {
  final lock = withLock ? 'topiaforge.multiplayer.lock.json' : 'rules.json';
  return ModManifest.fromJson({
    'schemaVersion': 6,
    'name': id,
    'version': version,
    'displayName': 'Fixture $id',
    'author': {'name': 'TopiaForge Tests'},
    'entryAssembly': 'Fixture.dll',
    'entryType': 'Fixtures.Mod',
    'supportedGameVersionRange': '*',
    'supportedLoaderVersionRange': loaderRange,
    'supportedSdkVersionRange': sdkRange,
    if (capabilities.isNotEmpty) 'capabilities': capabilities,
    if (platforms.isNotEmpty) 'platforms': platforms,
    if (mode == 'session') ...{
      'multiplayer': {
        'mode': 'session',
        'presence': presence,
        'protocol': {'version': '1.0.0'},
        'synchronizedFiles': [lock],
      },
      'hashes': {lock: 'a' * 64},
    } else if (mode != null)
      'multiplayer': {'mode': mode},
  });
}

DescriptorPackage testPackage(
  String id, {
  String version = '1.0.0',
  DescriptorPackageRole role = DescriptorPackageRole.implicit,
}) => DescriptorPackage(
  id: id,
  version: version,
  sha256: testSha(id, version),
  role: role,
);

/// Host A's descriptor with [packages], sorted by id.
ServerDescriptorV1 testDescriptor({
  List<DescriptorPackage> packages = const [],
  String name = "Charl's Sandbox",
  String gameBuild = '2545',
  String runtimeVersion = '0.2.0',
  String protocolVersion = '1.0.0',
  bool voice = true,
  bool hostServesPackages = false,
  int issuedAt = 1791849600,
}) => ServerDescriptorV1(
  signingKey: WireText.tryParseBase64Url(hostASigningKey, 32)!,
  transportKey: WireText.tryParseBase64Url(hostATransportKey, 32)!,
  issuedAt: issuedAt,
  name: name,
  gameBuild: gameBuild,
  runtimeVersion: runtimeVersion,
  protocolVersion: protocolVersion,
  maxPlayers: 8,
  passwordRequired: false,
  voice: voice,
  visibility: ServerVisibility.public,
  hostServesPackages: hostServesPackages,
  gamemode: 'Open Sandbox',
  world: 'Test City',
  packages: [...packages]..sort((a, b) => a.id.compareTo(b.id)),
);

JoinPackageResolution installedOf(ModManifest manifest) =>
    JoinPackageResolution.installed(manifest);

JoinPackageResolution availableOf(ModManifest manifest, {int? size}) =>
    JoinPackageResolution.available(
      manifest: manifest,
      sourceId: 'io.github.furroxide.topiaforge.official',
      sourceName: 'TopiaForge Mod Registry',
      downloadUrl: 'https://docs.topiaforge.dev/registry/${manifest.id}.zip',
      sizeBytes: size,
    );

/// Plans joining host A's server running [packages], each with what the
/// launcher found for it; a null resolution leaves the package unlisted.
Future<JoinPlan> planJoin(
  List<(DescriptorPackage, JoinPackageResolution?)> packages, {
  JoinEnvironment? environment,
  bool hostServesPackages = false,
  String gameBuild = '2545',
  String runtimeVersion = '0.2.0',
  String protocolVersion = '1.0.0',
  bool voice = true,
}) async {
  final descriptor = testDescriptor(
    packages: [for (final (package, _) in packages) package],
    hostServesPackages: hostServesPackages,
    gameBuild: gameBuild,
    runtimeVersion: runtimeVersion,
    protocolVersion: protocolVersion,
    voice: voice,
  );
  return const JoinPlanner().plan(
    server: await verifiedForTest(descriptor),
    resolutions: {
      for (final (package, resolution) in packages) package.sha256: ?resolution,
    },
    environment: environment ?? testEnvironment,
  );
}

/// Starts a join of [target], relay-only unless [privacy] says otherwise.
JoinSessionState startJoin(
  JoinTarget target, {
  MultiplayerAddressPrivacy privacy = MultiplayerAddressPrivacy.relayOnly,
  bool trustChangedIdentity = false,
}) => JoinSession.start(
  target,
  privacy: privacy,
  trustChangedIdentity: trustChangedIdentity,
);

JoinPlanPackage packageIn(JoinPlan plan, String id) =>
    plan.packages.singleWhere((item) => item.package.id == id);
