part of 'join_flow_integration_test.dart';

/// A game folder the launcher accepts, with a runtime repaired into it, a
/// data folder, and the player's https source answered from 127.0.0.1.
final class _Rig {
  _Rig._(this.root);

  final Directory root;
  late final LocalLauncherRepository repository;
  late GameInstall install;
  final game = _RecordingGame();
  final served = <String>[];
  final hostsAsked = <String>{};
  late final HttpServer _server;

  String get gameRoot => p.join(root.path, 'Game');
  String get dataRoot => p.join(root.path, 'data');

  static Future<_Rig> create() async {
    final root = Directory(
      Directory.systemTemp
          .createTempSync('tf-join-app-')
          .resolveSymbolicLinksSync(),
    );
    addTearDown(() => root.deleteSync(recursive: true));
    final rig = _Rig._(root);
    final managed = Directory(p.join(rig.gameRoot, 'Robotopia_Data', 'Managed'))
      ..createSync(recursive: true);
    File(p.join(managed.path, 'UnityEngine.dll')).writeAsStringSync('');
    File(p.join(rig.gameRoot, 'Robotopia.exe')).writeAsStringSync('');
    File(
      p.join(rig.gameRoot, 'installed-build.json'),
    ).writeAsStringSync(jsonEncode({'id': '2545'}));
    final repositoryRoot = p.join(root.path, 'repo');
    _runtimeSources(repositoryRoot);
    rig.repository = LocalLauncherRepository(
      dataRoot: rig.dataRoot,
      repositoryRoot: repositoryRoot,
      packageMetadataValidator: (_) async => const [],
      gameRunningProbe: (_) async => false,
      gameProcessCreator: (request) => rig.game.start(request, rig.gameRoot),
      gameProcessLiveness: (_) async => true,
    );
    addTearDown(rig.repository.dispose);
    rig.install = await rig.repository.selectGameDirectory(rig.gameRoot);
    final repair = await rig.repository.installOrRepairRuntime(rig.install);
    expect(repair.ok, isTrue, reason: 'the test runtime installs');
    rig.install = await rig.repository.selectGameDirectory(rig.gameRoot);
    expect(rig.install.gameVersion, isNotNull);
    final settingsFile = File(p.join(rig.dataRoot, 'settings.json'));
    final settings =
        jsonDecode(settingsFile.readAsStringSync()) as Map<String, Object?>;
    // A Windows build on Linux or macOS starts under this command.
    settings['wineCommand'] = Platform.resolvedExecutable;
    settingsFile.writeAsStringSync(jsonEncode(settings));
    return rig;
  }

  /// Serves [archives] and their index from 127.0.0.1, as
  /// `https://mods.example.test`, and makes that the player's one source.
  Future<void> serve(List<_Archive> archives) async {
    const origin = 'https://mods.example.test';
    final index = utf8.encode(
      jsonEncode({
        'packages': {
          for (final archive in archives)
            archive.id: {
              'versions': {
                archive.version: {
                  ...archive.manifest,
                  'url': '$origin/${archive.fileName}',
                  'zipSHA256': archive.sha256,
                },
              },
            },
        },
      }),
    );
    _server = await HttpServer.bind(InternetAddress.loopbackIPv4, 0);
    addTearDown(() => _server.close(force: true));
    _server.listen((request) async {
      served.add(request.uri.path);
      final body = request.uri.path == '/index.json'
          ? index
          : archives
                .where((archive) => request.uri.path == '/${archive.fileName}')
                .firstOrNull
                ?.bytes;
      final response = request.response;
      if (body == null) {
        response.statusCode = HttpStatus.notFound;
      } else {
        response.contentLength = body.length;
        response.add(body);
      }
      await response.close();
    });
    HttpOverrides.global = _Loopback(_server.port, hostsAsked);
    addTearDown(() => HttpOverrides.global = null);
    await repository.savePackageSources([
      const PackageSource(
        id: 'join.source',
        name: 'Join Source',
        url: '$origin/index.json',
      ),
    ]);
  }

  LocalMultiplayerRepository multiplayer(ControlPlaneClient service) {
    final multiplayer = LocalMultiplayerRepository(
      dataRoot: dataRoot,
      service: MultiplayerServiceConfiguration.parse('https://play.example'),
      controlPlane: service,
      content: repository,
      gameProbe: (_) async => false,
    );
    addTearDown(multiplayer.dispose);
    return multiplayer;
  }

  static void _runtimeSources(String repositoryRoot) {
    final bepinex = p.join(
      repositoryRoot,
      'third_party',
      'BepInEx',
      'win_x64_5.4.23.5',
    );
    Directory(p.join(bepinex, 'BepInEx', 'core')).createSync(recursive: true);
    for (final file in [
      'winhttp.dll',
      'doorstop_config.ini',
      'BepInEx/core/BepInEx.dll',
    ]) {
      File(p.join(bepinex, file)).writeAsStringSync('');
    }
    final loader = Directory(
      p.join(
        repositoryRoot,
        'src',
        'TopiaForge.ModManager',
        'bin',
        'Release',
        'netstandard2.1',
      ),
    )..createSync(recursive: true);
    for (final dll in topiaForgeRuntimeLoaderDlls) {
      File(p.join(loader.path, dll)).writeAsStringSync('');
    }
  }
}

/// Every HTTPS client the launcher makes reaches the server on 127.0.0.1
/// as plain HTTP; the hosts it asked for are kept. Nothing leaves the
/// machine.
final class _Loopback extends HttpOverrides {
  _Loopback(this.port, this.hosts);

  final int port;
  final Set<String> hosts;

  @override
  HttpClient createHttpClient(SecurityContext? context) =>
      super.createHttpClient(context)
        ..findProxy = ((_) => 'DIRECT')
        ..connectionFactory = (uri, proxyHost, proxyPort) {
          hosts.add(uri.host);
          return Socket.startConnect(InternetAddress.loopbackIPv4, port);
        };
}

/// The service, as far as a server link needs it: it resolves the link and
/// hands over the server's verified descriptor.
final class _Service implements ControlPlaneClient {
  _Service(this.server);

  final VerifiedServerDescriptor server;
  static const _rest = UnavailableControlPlane(
    MultiplayerServiceStatus.configured,
  );

  @override
  MultiplayerServiceStatus get status => MultiplayerServiceStatus.configured;

  @override
  Future<MultiplayerServiceCheck> readStatus({
    CancellationToken? cancellation,
  }) => _rest.readStatus(cancellation: cancellation);

  @override
  Future<InternetBrowseResult> browse(
    ServerBrowseFilter filter, {
    String? after,
    CancellationToken? cancellation,
  }) => _rest.browse(filter, after: after, cancellation: cancellation);

  @override
  Future<InternetServerLookup> fetchServer(
    String serverId, {
    CancellationToken? cancellation,
  }) => _rest.fetchServer(serverId, cancellation: cancellation);

  @override
  Future<JoinEvent?> resolve(
    JoinTarget target, {
    CancellationToken? cancellation,
  }) async => JoinTargetResolved(server.serverId);

  @override
  Future<JoinEvent?> fetchDescriptor(
    JoinDescriptorRequest request, {
    CancellationToken? cancellation,
  }) async => JoinDescriptorVerified(server);
}

final class _AcceptAll implements ServerDescriptorSignatureVerifier {
  const _AcceptAll();

  @override
  Future<bool> verifySignature(SignedServerDescriptorV1 envelope) async => true;
}

/// Host A's server running [packages]; the fake service vouches for it.
Future<VerifiedServerDescriptor> _descriptor(
  List<DescriptorPackage> packages,
) async {
  final descriptor = ServerDescriptorV1(
    // Host A's keys, from `tests/fixtures/multiplayer/descriptor/`.
    signingKey: WireText.tryParseBase64Url(
      'X_63eOUQF6uSrrIZ1DhX5w-ouzCDzlMBavIFLxvmABE',
      32,
    )!,
    transportKey: WireText.tryParseBase64Url(
      'RIcUrvBv2CmtHVUuyjEvJ7SN1V5vghS4UWaimHb8Y2s',
      32,
    )!,
    issuedAt: 1791849600,
    name: "Sam's Sandbox",
    gameBuild: '2545',
    runtimeVersion: TopiaForgeRuntimeVersions.loaderVersion,
    protocolVersion: TopiaForgeRuntimeVersions.multiplayerProtocolVersion,
    maxPlayers: 8,
    passwordRequired: false,
    voice: false,
    visibility: ServerVisibility.public,
    hostServesPackages: false,
    gamemode: 'Open Sandbox',
    world: 'Test City',
    packages: [...packages]..sort((a, b) => a.id.compareTo(b.id)),
  );
  final envelope = SignedServerDescriptorV1.assemble(
    descriptor,
    Uint8List(SignedServerDescriptorV1.signatureBytes),
  );
  final result = await const ServerDescriptorVerifier(
    _AcceptAll(),
  ).verify(envelope.bytes);
  return (result as ServerDescriptorAccepted).descriptor;
}

/// A game that records what the launcher staged as it started: the launch
/// request, which it deletes once read as the game does, and the copy of
/// the preferences it reads at start.
final class _RecordingGame {
  final requests = <String>[];
  final projections = <String?>[];

  Future<LaunchProcessReceipt> start(
    GameProcessRequest request,
    String gameRoot,
  ) async {
    final file = File(
      request.environment[ProfileLaunchConfigurationV5.environmentVariable]!,
    );
    requests.add(file.readAsStringSync());
    file.deleteSync();
    final projection = File(
      p.join(
        gameRoot,
        'BepInEx',
        'TopiaForge',
        'staging',
        'multiplayer-preferences-v1.json',
      ),
    );
    projections.add(
      projection.existsSync() ? projection.readAsStringSync() : null,
    );
    final identity = LaunchProcessIdentity(
      pid: 4300 + requests.length,
      executablePath: p.join(gameRoot, 'Robotopia.exe'),
      startTimeUtc: DateTime.utc(2026, 10, 25),
      nativeStartToken: 'generation:${requests.length}',
    );
    return LaunchProcessReceipt(pid: identity.pid, identity: identity);
  }
}

/// A mod archive as a source serves it: a V6 manifest and one assembly,
/// stored uncompressed, with the same bytes on every run.
final class _Archive {
  _Archive(this.id, this.version)
    : manifest = {
        'schemaVersion': 6,
        'name': id,
        'displayName': 'Mod $id',
        'version': version,
        'author': {'name': 'TopiaForge Tests'},
        'entryAssembly': '${_assembly(id)}.dll',
        'entryType': '$id.Entry',
        'supportedGameVersionRange': '*',
        'supportedLoaderVersionRange': '*',
        'supportedSdkVersionRange': '*',
      } {
    bytes = _storedZip({
      'topiaforge.mod.json': utf8.encode(jsonEncode(manifest)),
      '${_assembly(id)}.dll': utf8.encode('$id $version'),
    });
  }

  final String id;
  final String version;
  final Map<String, Object?> manifest;
  late final Uint8List bytes;

  String get sha256 => PureSha256.hex(bytes);

  String get fileName => '$id-$version.topiaforgemod';

  DescriptorPackage get package => DescriptorPackage(
    id: id,
    version: version,
    sha256: sha256,
    role: DescriptorPackageRole.implicit,
  );

  static String _assembly(String id) => id
      .split('.')
      .map((part) => part[0].toUpperCase() + part.substring(1))
      .join();
}

/// A zip of [files], each stored as it is and dated 1 October 2026.
Uint8List _storedZip(Map<String, List<int>> files) {
  const date = (46 << 9) | (10 << 5) | 1;
  final local = BytesBuilder();
  final central = BytesBuilder();
  for (final MapEntry(key: name, value: data) in files.entries) {
    final path = ascii.encode(name);
    final crc = _crc32(data);
    final offset = local.length;
    final header = ByteData(30)
      ..setUint32(0, 0x04034b50, Endian.little)
      ..setUint16(4, 20, Endian.little)
      ..setUint16(12, date, Endian.little)
      ..setUint32(14, crc, Endian.little)
      ..setUint32(18, data.length, Endian.little)
      ..setUint32(22, data.length, Endian.little)
      ..setUint16(26, path.length, Endian.little);
    local
      ..add(header.buffer.asUint8List())
      ..add(path)
      ..add(data);
    final entry = ByteData(46)
      ..setUint32(0, 0x02014b50, Endian.little)
      ..setUint16(4, 20, Endian.little)
      ..setUint16(6, 20, Endian.little)
      ..setUint16(14, date, Endian.little)
      ..setUint32(16, crc, Endian.little)
      ..setUint32(20, data.length, Endian.little)
      ..setUint32(24, data.length, Endian.little)
      ..setUint16(28, path.length, Endian.little)
      ..setUint32(42, offset, Endian.little);
    central
      ..add(entry.buffer.asUint8List())
      ..add(path);
  }
  final end = ByteData(22)
    ..setUint32(0, 0x06054b50, Endian.little)
    ..setUint16(8, files.length, Endian.little)
    ..setUint16(10, files.length, Endian.little)
    ..setUint32(12, central.length, Endian.little)
    ..setUint32(16, local.length, Endian.little);
  return (BytesBuilder()
        ..add(local.takeBytes())
        ..add(central.takeBytes())
        ..add(end.buffer.asUint8List()))
      .takeBytes();
}

int _crc32(List<int> data) {
  var crc = 0xffffffff;
  for (final byte in data) {
    crc ^= byte;
    for (var bit = 0; bit < 8; bit++) {
      crc = crc & 1 == 1 ? (crc >>> 1) ^ 0xedb88320 : crc >>> 1;
    }
  }
  return crc ^ 0xffffffff;
}
