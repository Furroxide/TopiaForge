import 'dart:convert';
import 'dart:io';

import 'package:launcher_domain/launcher_domain.dart';
import 'package:path/path.dart' as p;

import 'dev_peer_overlays.dart';
import 'launch_process_control.dart';
import 'launch_staging_store.dart';
import 'local_launcher_repository.dart';
import 'multiplayer/descriptor_client.dart';

/// Owns one process receipt and its isolated repository throughout a dev run.
final class DevPeerSession {
  DevPeerSession(
    this.overlay,
    this.runId,
    String repositoryRoot, {
    LocalLauncherRepository? repository,
    this.cancellation,
  }) : repository =
           repository ??
           LocalLauncherRepository(
             dataRoot: overlay.launcherRoot,
             knownGamePath: overlay.gameRoot,
             repositoryRoot: repositoryRoot,
           );

  final DevPeerOverlay overlay;
  final String runId;
  final LocalLauncherRepository repository;
  final CancellationToken? cancellation;
  GameInstall? _install;
  LauncherProfile? _profile;
  LaunchResult? _launch;

  Future<void> prepare(
    List<String> packages,
    LauncherProfile source,
    String modId,
    LaunchSelection? selection,
  ) async {
    cancellation?.throwIfCancelled();
    final install = await repository.detectKnownInstall();
    if (install == null) {
      throw StateError(
        'The development game overlay is incomplete. Repair the source installation.',
      );
    }
    cancellation?.throwIfCancelled();
    final repair = await repository.installOrRepairRuntime(install);
    if (!repair.ok) {
      throw StateError(
        'The development runtime could not be installed. Build the runtime and retry.',
      );
    }
    for (final package in packages) {
      cancellation?.throwIfCancelled();
      await repository.installPackage(package, install);
    }
    cancellation?.throwIfCancelled();
    final snapshot = await repository.loadSnapshot();
    final enabled = <String>{
      if (source.inheritManagerModState)
        ...snapshot.installedMods
            .where((mod) => mod.enabled)
            .map((mod) => mod.id)
      else
        ...source.enabledMods,
      modId,
      'io.github.furroxide.topiaforge.multiplayer',
    };
    // Explicit profiles still need the required closure of the mod being
    // developed. Optional dependencies keep the profile's own choice.
    var changed = true;
    while (changed) {
      changed = false;
      for (final mod in snapshot.installedMods.where(
        (mod) => enabled.contains(mod.id),
      )) {
        for (final dependency
            in mod.manifest?.dependencies ?? <ModDependency>[]) {
          if (!dependency.optional && enabled.add(dependency.id)) {
            changed = true;
          }
        }
      }
    }
    final profile = source.copyWith(
      id: 'dev-peer',
      name: 'Development peer',
      revision: 0,
      inheritManagerModState: false,
      enabledMods: enabled,
      launchSelection: selection ?? source.launchSelection,
      launchSettings: LaunchSettings(
        extraArguments: [
          '-screen-width',
          '2560',
          '-screen-height',
          '1440',
          '-screen-fullscreen',
          '0',
          '-logFile',
          overlay.playerLog,
        ],
        environment: {
          'TOPIAFORGE_LIVE_MULTIPLAYER': '1',
          'TOPIAFORGE_DEV_PEERS': runId,
          'TOPIAFORGE_DATA_ROOT': overlay.launcherRoot,
          // Do not inherit the obsolete environment-driven host/join switch.
          'TOPIAFORGE_MULTIPLAYER_HOST': '', 'TOPIAFORGE_MULTIPLAYER_JOIN': '',
        },
      ),
    );
    final saved = await repository.saveProfiles([profile], profile.id);
    _profile = saved.single;
    _install = await repository.detectKnownInstall();
    if (_install == null) {
      throw StateError(
        'The development overlay disappeared during preparation. Restart the dev loop.',
      );
    }
  }

  Future<void> launch(int count, {LaunchSessionTarget? target}) async {
    cancellation?.throwIfCancelled();
    if (!Platform.isWindows) {
      throw UnsupportedError(
        'Development peers require Windows process creation receipts. No peer was started.',
      );
    }
    final request = requestFor(count, target: target);
    final result = await repository.launchSession(
      _install!,
      _profile!,
      request,
    );
    _launch = result;
    if (!result.processStarted ||
        result.process == null ||
        result.requestId == null) {
      throw StateError(
        'The development peer did not start. Check its package dependencies and runtime installation.',
      );
    }
  }

  /// The LAN wire mode permits a local join. The provider's development
  /// factory suppresses all discovery and binds loopback regardless of it.
  LaunchSessionRequest requestFor(int count, {LaunchSessionTarget? target}) {
    final settings = MultiplayerSettings(
      displayName: 'Peer ${overlay.index + 1}',
      lanDiscoveryEnabled: true,
    );
    final intent = '$runId-${overlay.index}';
    final privacy = settings.privacyForSession(allowDirect: false);
    final peer = LaunchDevInstance(index: overlay.index, count: count);
    return target == null
        ? LaunchSessionRequest.host(
            intentId: intent,
            settings: settings,
            privacy: privacy,
            host: LaunchHostOptions(
              name: 'Local development',
              maxPlayers: count,
            ),
            port: 0,
            devInstance: peer,
            developerMode: true,
          )
        : LaunchSessionRequest.join(
            intentId: intent,
            settings: settings,
            privacy: privacy,
            target: target,
            devInstance: peer,
            developerMode: true,
          );
  }

  Future<bool?> isAlive() async => _launch?.process == null
      ? (_launch?.processStarted == true ? null : false)
      : isLaunchProcessAlive(_launch!.process!);

  Future<bool> isReady() async {
    final request = _launch?.requestId;
    if (request == null) return false;
    final store = LaunchStagingStore(overlay.gameRoot);
    final outcome = await store.readOutcome(request, session: false);
    final ended = await store.readOutcome(request, session: true);
    if (ended != null || (outcome != null && outcome.status != 'succeeded')) {
      throw StateError(
        'The development peer reported a failed, cancelled or ended session. Inspect its log and retry.',
      );
    }
    final progress = await store.readProgress(request);
    final state = progress?.session?.state;
    if (state == LaunchSessionState.stopped ||
        state == LaunchSessionState.ended) {
      throw StateError(
        'The development peer session ended before startup completed.',
      );
    }
    return state == LaunchSessionState.hosting ||
        state == LaunchSessionState.inSession;
  }

  /// A fresh run-bound port receipt is followed by a signed descriptor fetch.
  /// Neither the target nor socket failures are printed by the dev loop.
  Future<LaunchSessionTarget?> hostTarget() async {
    if (!await isReady() || await isAlive() != true) return null;
    final file = File(
      p.join(
        overlay.gameRoot,
        'BepInEx',
        'TopiaForge',
        'data',
        'io.github.furroxide.topiaforge.multiplayer',
        'dev-session.json',
      ),
    );
    if (FileSystemEntity.typeSync(file.path, followLinks: false) !=
            FileSystemEntityType.file ||
        file.lengthSync() > 4096) {
      return null;
    }
    Object? decoded;
    try {
      decoded = jsonDecode(file.readAsStringSync());
    } on FormatException {
      return null;
    }
    if (decoded is! Map<String, Object?> ||
        decoded['schemaVersion'] != 1 ||
        decoded['runId'] != runId ||
        decoded['port'] is! int ||
        decoded['serverId'] is! String) {
      return null;
    }
    final port = decoded['port'] as int;
    if (port < 1 || port > 65535) return null;
    final fetched = await DescriptorClient().fetchFrom(
      InternetAddress.loopbackIPv4,
      port,
      allowDirect: false,
      expectedServerId: decoded['serverId'] as String,
      cancellation: cancellation,
    );
    if (fetched is! DescriptorFetched) return null;
    final descriptor = fetched.descriptor;
    return LaunchDirectTarget(
      host: '127.0.0.1',
      port: port,
      serverId: descriptor.serverId,
      descriptorSha256: descriptor.descriptorSha256,
      packageDigest: descriptor.packageDigest,
    );
  }

  Future<List<String>> recentLog() async {
    if (_install == null) return const [];
    final text = await repository.readRecentLog(_install!, maxLines: 100);
    return text.isEmpty ? const [] : text.split('\n');
  }

  Future<void> close() async {
    try {
      final process = _launch?.process;
      if (_launch?.processStarted == true && process == null) {
        throw StateError(
          'A started peer has no ownership receipt; its files are retained for manual cleanup.',
        );
      }
      if (process != null) {
        final alive = await isLaunchProcessAlive(process);
        if (alive == null) {
          throw StateError(
            'A peer could not be identified; its files are retained for manual cleanup.',
          );
        }
        if (alive) await stopLaunchProcess(process);
        if (await isLaunchProcessAlive(process) != false) {
          throw StateError(
            'A peer did not stop; its files are retained for manual cleanup.',
          );
        }
      }
    } finally {
      await repository.dispose();
    }
  }
}
