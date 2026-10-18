/// Fixtures for the join content tests: a game folder, mod archives built in
/// memory, verified server descriptors, plans and join states. Everything
/// lives in a temporary folder, and nothing here uses `package:test`, so the
/// crash child process can build the same join.
library;

import 'dart:convert';
import 'dart:io';
import 'dart:typed_data';

import 'package:archive/archive.dart';
import 'package:crypto/crypto.dart';
import 'package:launcher_domain/launcher_domain.dart';
import 'package:path/path.dart' as p;

/// Host A's keys from `tests/fixtures/multiplayer/descriptor/cases.json`.
const testSigningKey = 'X_63eOUQF6uSrrIZ1DhX5w-ouzCDzlMBavIFLxvmABE';
const testTransportKey = 'RIcUrvBv2CmtHVUuyjEvJ7SN1V5vghS4UWaimHb8Y2s';

final class AcceptAllSignatures implements ServerDescriptorSignatureVerifier {
  const AcceptAllSignatures();

  @override
  Future<bool> verifySignature(SignedServerDescriptorV1 envelope) async => true;
}

/// The environment every plan in these tests is made for.
final testJoinEnvironment = JoinEnvironment(
  gameBuild: '2545',
  runtimeVersion: '0.2.0',
  sdkVersion: '0.2.0',
  protocolVersion: '1.0.0',
  platform: 'windows',
  architecture: 'x64',
);

/// A mod archive built in memory, with a V6 manifest and one assembly.
final class TestArchive {
  TestArchive(
    this.id,
    this.version, {
    String? name,
    String payload = '',
    Map<String, Object?> manifestChanges = const {},
  }) : manifestJson = {
         'schemaVersion': 6,
         'name': id,
         'displayName': name ?? 'Mod $id',
         'version': version,
         'author': {'name': 'TopiaForge Tests'},
         'entryAssembly': '${_assemblyName(id)}.dll',
         'entryType': '$id.Entry',
         'supportedGameVersionRange': '*',
         'supportedLoaderVersionRange': '*',
         'supportedSdkVersionRange': '*',
         ...manifestChanges,
       } {
    final archive = Archive()
      ..addFile(
        ArchiveFile.string('topiaforge.mod.json', jsonEncode(manifestJson)),
      )
      ..addFile(
        ArchiveFile.string(
          '${_assemblyName(id)}.dll',
          payload.isEmpty ? '$id $version' : payload,
        ),
      );
    // A fixed time, so the same archive has the same SHA-256 in every
    // process.
    bytes = Uint8List.fromList(
      ZipEncoder().encode(archive, modified: DateTime.utc(2026, 10, 1)),
    );
  }

  final String id;
  final String version;
  final Map<String, Object?> manifestJson;
  late final Uint8List bytes;

  String get sha256Hex => sha256.convert(bytes).toString();

  /// The manifest a source lists for this archive.
  ModManifest get manifest => ModManifest.fromJson(manifestJson);

  String get fileName => '$id-$version.topiaforgemod';

  File writeTo(Directory folder) =>
      File(p.join(folder.path, fileName))..writeAsBytesSync(bytes);

  DescriptorPackage get package => DescriptorPackage(
    id: id,
    version: version,
    sha256: sha256Hex,
    role: DescriptorPackageRole.implicit,
  );

  /// Offered by a source at [url], with [size] stated when given.
  JoinPackageResolution offeredAt(String url, {int? size, String? source}) =>
      JoinPackageResolution.available(
        manifest: manifest,
        sourceId: 'test.source',
        sourceName: source ?? 'Test Source',
        downloadUrl: url,
        sizeBytes: size,
      );
}

/// [length] letters that do not compress, the same for the same [seed], so
/// an archive holding them is about that large.
String noisyPayload(int length, {int seed = 7}) {
  var state = seed;
  final units = List<int>.generate(length, (_) {
    state = (state * 1103515245 + 12345) & 0x7fffffff;
    return 0x21 + (state >> 16) % 94;
  });
  return String.fromCharCodes(units);
}

String _assemblyName(String id) => id
    .split(RegExp(r'[^A-Za-z0-9]+'))
    .where((part) => part.isNotEmpty)
    .map((part) => part[0].toUpperCase() + part.substring(1))
    .join();

/// A verified descriptor of host A's server, running [packages].
Future<VerifiedServerDescriptor> verifiedServer(
  List<DescriptorPackage> packages, {
  String name = "Sam's Sandbox",
  bool hostServesPackages = false,
  int issuedAt = 1791849600,
  List<int>? signingKey,
}) async {
  final descriptor = ServerDescriptorV1(
    signingKey: signingKey ?? WireText.tryParseBase64Url(testSigningKey, 32)!,
    transportKey: WireText.tryParseBase64Url(testTransportKey, 32)!,
    issuedAt: issuedAt,
    name: name,
    gameBuild: '2545',
    runtimeVersion: '0.2.0',
    protocolVersion: '1.0.0',
    maxPlayers: 8,
    passwordRequired: false,
    voice: false,
    visibility: ServerVisibility.public,
    hostServesPackages: hostServesPackages,
    gamemode: 'Open Sandbox',
    world: 'Test City',
    packages: [...packages]..sort((a, b) => a.id.compareTo(b.id)),
  );
  final envelope = SignedServerDescriptorV1.assemble(
    descriptor,
    Uint8List(SignedServerDescriptorV1.signatureBytes),
  );
  final result = await const ServerDescriptorVerifier(
    AcceptAllSignatures(),
  ).verify(envelope.bytes);
  return (result as ServerDescriptorAccepted).descriptor;
}

/// Plans joining [server] with what the launcher found for each package.
JoinPlan planJoin(
  VerifiedServerDescriptor server,
  Map<String, JoinPackageResolution> resolutions, {
  bool developerMode = false,
}) => const JoinPlanner().plan(
  server: server,
  resolutions: resolutions,
  environment: developerMode
      ? JoinEnvironment(
          gameBuild: '2545',
          runtimeVersion: '0.2.0',
          sdkVersion: '0.2.0',
          protocolVersion: '1.0.0',
          platform: 'windows',
          architecture: 'x64',
          developerMode: true,
        )
      : testJoinEnvironment,
  mayContactHost: true,
);

/// A join of [server] after consent to [plan]: downloading, or preparing the
/// profile when the plan downloads nothing.
///
/// The join is of a server link, or, with [onLocalNetwork], of a direct link
/// to a host on the local network, which may serve its own packages. That
/// host is a documentation-range address nothing contacts.
JoinSessionState consentedJoin(
  VerifiedServerDescriptor server,
  JoinPlan plan, {
  bool onLocalNetwork = false,
  bool closeOwnedGame = false,
}) {
  if (!plan.isJoinable) {
    throw StateError('The plan is blocked: ${plan.blockers}');
  }
  final link = onLocalNetwork
      ? SessionLink.direct(
          serverId: server.serverId,
          host: '192.168.1.20',
          port: 27250,
        )
      : SessionLink.server(server.serverId);
  return [
    JoinTargetResolved(server.serverId),
    JoinDescriptorVerified(server),
    JoinPlanReady(plan),
    JoinConsentGiven(
      hostServedAcknowledged: plan.hasUnverifiedPackages,
      closeOwnedGameAcknowledged: closeOwnedGame,
    ),
  ].fold(
    JoinSession.start(JoinLinkTarget(link), privacy: SessionPrivacy.relayOnly),
    JoinSession.reduce,
  );
}

/// The same join once its downloads finished. [closeOwnedGame] is whether
/// the consent covered closing a game this launcher started.
JoinSessionState installingJoin(
  VerifiedServerDescriptor server,
  JoinPlan plan, {
  bool closeOwnedGame = false,
}) => JoinSession.reduce(
  consentedJoin(server, plan, closeOwnedGame: closeOwnedGame),
  const JoinDownloadsCompleted(),
);

/// The same join once its install finished.
JoinSessionState preparingJoin(VerifiedServerDescriptor server, JoinPlan plan) {
  final consented = consentedJoin(server, plan);
  return consented.stage == JoinStage.preparingProfile
      ? consented
      : JoinSession.reduce(
          JoinSession.reduce(consented, const JoinDownloadsCompleted()),
          const JoinInstallCompleted(),
        );
}

/// A game folder the launcher accepts.
Directory createTestGame(Directory parent) {
  final game = Directory(p.join(parent.path, 'Game'))..createSync();
  File(p.join(game.path, 'Robotopia.exe')).writeAsStringSync('');
  Directory(
    p.join(game.path, 'Robotopia_Data', 'Managed'),
  ).createSync(recursive: true);
  File(
    p.join(game.path, 'Robotopia_Data', 'Managed', 'UnityEngine.dll'),
  ).writeAsStringSync('');
  return game;
}

/// The cache file a join keeps [archive] in, under [dataRoot].
File cachedArchive(String dataRoot, TestArchive archive) => File(
  p.join(dataRoot, 'package-cache', '${archive.sha256Hex}.topiaforgemod'),
);

/// A version folder of [game].
Directory packageFolder(Directory game, String id, String version) => Directory(
  p.join(game.path, 'BepInEx', 'TopiaForge', 'packages', id, version),
);

File managerStateFile(Directory game) =>
    File(p.join(game.path, 'BepInEx', 'TopiaForge', 'state.json'));

File serverContentFile(Directory game) =>
    File(p.join(game.path, 'BepInEx', 'TopiaForge', 'server-content.json'));

Directory managerStaging(Directory game) =>
    Directory(p.join(game.path, 'BepInEx', 'TopiaForge', 'staging'));

/// The join folders an install left in manager staging.
List<String> joinFolders(Directory game) {
  final staging = managerStaging(game);
  if (!staging.existsSync()) return const [];
  return [
    for (final entity in staging.listSync())
      if (entity is Directory &&
          (p.basename(entity.path).startsWith('launcher-join-') ||
              p.basename(entity.path).startsWith('launcher-trash-')))
        p.basename(entity.path),
  ];
}

/// The manager state's records, each as canonical text, by lowercase id.
Map<String, String> managerRecords(Directory game) {
  final file = managerStateFile(game);
  if (!file.existsSync()) return const {};
  final state = jsonDecode(file.readAsStringSync()) as Map<String, Object?>;
  return {
    for (final record in (state['mods'] as List? ?? const []).cast<Map>())
      (record['id'] as String).toLowerCase(): canonicalText(record),
  };
}

/// [value] as JSON with every object's members sorted, for comparisons that
/// must not depend on member order.
String canonicalText(Object? value) {
  if (value is Map) {
    final keys = value.keys.map((key) => '$key').toList()..sort();
    return '{${[for (final key in keys) '${jsonEncode(key)}:${canonicalText(value[key])}'].join(',')}}';
  }
  if (value is List) return '[${value.map(canonicalText).join(',')}]';
  return jsonEncode(value);
}

/// The join most tests make, against a player who already has Alpha 1.0.0
/// (on), Beta 1.0.0 (off) and Gamma 1.0.0, and three profiles of their own.
///
/// The server runs Alpha 2.0.0, a newer version of a mod the player leaves
/// unpinned; Beta 1.5.0, of a mod they turned off; Delta 1.0.0, new to them;
/// and Gamma at the exact build they have.
final class StandardJoin {
  static final alpha = TestArchive('own.alpha', '1.0.0', name: 'Alpha');
  static final beta = TestArchive('own.beta', '1.0.0', name: 'Beta');
  static final gamma = TestArchive('own.gamma', '1.0.0', name: 'Gamma');
  static final serverAlpha = TestArchive('own.alpha', '2.0.0', name: 'Alpha');
  static final serverBeta = TestArchive('own.beta', '1.5.0', name: 'Beta');
  static final delta = TestArchive('server.delta', '1.0.0', name: 'Delta');

  static List<TestArchive> get downloads => [serverAlpha, serverBeta, delta];

  static Future<(VerifiedServerDescriptor, JoinPlan)> plan({
    String name = "Sam's Sandbox",
  }) async {
    final server = await verifiedServer([
      for (final archive in [...downloads, gamma]) archive.package,
    ], name: name);
    return (
      server,
      planJoin(server, {
        for (final archive in downloads)
          archive.sha256Hex: archive.offeredAt(
            'https://packages.example/${archive.fileName}',
          ),
        gamma.sha256Hex: JoinPackageResolution.installed(gamma.manifest),
      }),
    );
  }
}
