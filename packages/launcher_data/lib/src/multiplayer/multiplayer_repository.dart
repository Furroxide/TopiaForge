/// The launcher's multiplayer facade over its network clients and store.
library;

import 'dart:async';

import 'package:launcher_domain/launcher_domain.dart';
import 'package:path/path.dart' as p;

import '../cancellation_token.dart';
import 'control_plane.dart';
import 'descriptor_client.dart';
import 'join_descriptor_resolver.dart';
import 'lan_discovery_client.dart';
import 'lan_discovery_state.dart';
import 'multiplayer_store.dart';
import 'network_diagnostics.dart';

/// What the launcher's bloc asks of multiplayer: the player's settings,
/// favorites and history; LAN and Internet browsing; the join stages these
/// clients answer; and network diagnostics.
///
/// Nothing it returns names an address. Listings, bookmarks, events and
/// reports name servers by server id; where a LAN server was heard stays in
/// the LAN client, and a typed address stays in the join target the player
/// typed.
abstract interface class MultiplayerRepository {
  /// Whether this build names a multiplayer service.
  MultiplayerServiceStatus get serviceStatus;

  /// Reads the settings, recovering from damage. Never throws.
  Future<MultiplayerSettingsLoad> loadSettings();

  /// Saves [settings]; turning LAN discovery off stops browsing. Throws
  /// [MultiplayerStoreException] when they cannot be written.
  Future<MultiplayerSettings> saveSettings(MultiplayerSettings settings);

  /// Adds a favorite, or refreshes its name. Throws [StateError] when the
  /// favorites are full, since favorites are never dropped silently.
  Future<MultiplayerSettings> addFavorite({
    required String serverId,
    required String name,
  });

  Future<MultiplayerSettings> removeFavorite(String serverId);

  /// Moves a joined server to the front of the history.
  Future<MultiplayerSettings> recordJoin({
    required String serverId,
    required String name,
  });

  Future<MultiplayerSettings> removeFromHistory(String serverId);

  Future<MultiplayerSettings> clearHistory();

  LanBrowseSnapshot get lanSnapshot;

  /// Every change to LAN browsing.
  Stream<LanBrowseSnapshot> get lanSnapshots;

  /// Starts LAN browsing unless the player turned it off. Never throws.
  Future<LanBrowseSnapshot> startLanBrowsing();

  Future<LanBrowseSnapshot> stopLanBrowsing();

  /// A page of the Internet server browser, or why it is unavailable.
  Future<InternetBrowseResult> browseInternet({
    String? after,
    CancellationToken? cancellation,
  });

  /// Answers [state] when it waits on these clients: resolving a target
  /// through the service, or fetching its descriptor. Null for any other
  /// stage, and when [cancellation] stopped the work.
  Future<JoinEvent?> answerJoin(
    JoinSessionState state, {
    CancellationToken? cancellation,
  });

  /// Answers a join in [JoinStage.resolving]. Only targets the service
  /// resolves ever resolve; any other is an [ArgumentError].
  Future<JoinEvent?> resolveTarget(
    JoinTarget target, {
    CancellationToken? cancellation,
  });

  /// Answers [request], keeping to its contact rules; null when cancelled.
  Future<JoinEvent?> fetchDescriptor(
    JoinDescriptorRequest request, {
    CancellationToken? cancellation,
  });

  /// Reports on the service, the ports and LAN discovery. Port checks bind
  /// sockets, so they run only with [checkPorts].
  Future<MultiplayerNetworkReport> checkNetwork({
    bool checkPorts = false,
    bool developerMode = false,
  });

  Future<void> dispose();
}

/// [MultiplayerRepository] over the launcher's data folder and real sockets.
///
/// The store lives in `<dataRoot>/multiplayer/`. The service is whatever
/// this build was compiled with; a build without one contacts no service at
/// all ([UnavailableControlPlane]).
final class LocalMultiplayerRepository implements MultiplayerRepository {
  LocalMultiplayerRepository({
    required String dataRoot,
    MultiplayerServiceConfiguration? service,
    ControlPlaneClient? controlPlane,
    MultiplayerStore? store,
    DescriptorClient? descriptors,
    LanDiscoveryClient? lan,
    HostLookup? lookup,
    MultiplayerNetworkDiagnostics? diagnostics,
    DateTime Function()? clock,
  }) : this._(
         controlPlane ??
             UnavailableControlPlane(
               (service ?? MultiplayerServiceConfiguration.fromEnvironment())
                   .status,
             ),
         store ?? MultiplayerStore(p.join(dataRoot, folderName), clock: clock),
         descriptors ?? DescriptorClient(),
         lan,
         lookup,
         diagnostics ?? MultiplayerNetworkDiagnostics(),
         clock ?? DateTime.now,
       );

  LocalMultiplayerRepository._(
    this._controlPlane,
    this._store,
    DescriptorClient descriptors,
    LanDiscoveryClient? lan,
    HostLookup? lookup,
    this._diagnostics,
    this._clock,
  ) : _lan = lan ?? LanDiscoveryClient(descriptors: descriptors) {
    _resolver = JoinDescriptorResolver(
      descriptors: descriptors,
      store: _store,
      controlPlane: _controlPlane,
      lan: _lan,
      lookup: lookup,
      clock: _clock,
    );
  }

  /// The folder beneath the launcher's data root that holds the store.
  static const folderName = 'multiplayer';

  final ControlPlaneClient _controlPlane;
  final MultiplayerStore _store;
  final LanDiscoveryClient _lan;
  final MultiplayerNetworkDiagnostics _diagnostics;
  final DateTime Function() _clock;
  late final JoinDescriptorResolver _resolver;

  @override
  MultiplayerServiceStatus get serviceStatus => _controlPlane.status;

  @override
  Future<MultiplayerSettingsLoad> loadSettings() => _store.loadSettings();

  @override
  Future<MultiplayerSettings> saveSettings(MultiplayerSettings settings) async {
    final saved = await _store.saveSettings(settings);
    if (!saved.lanDiscoveryEnabled) await _lan.stop(disabled: true);
    return saved;
  }

  @override
  Future<MultiplayerSettings> addFavorite({
    required String serverId,
    required String name,
  }) {
    final bookmark = _bookmark(serverId, name);
    return _store.updateSettings(
      (settings) => settings.withFavorites(settings.favorites.add(bookmark)),
    );
  }

  @override
  Future<MultiplayerSettings> removeFavorite(String serverId) =>
      _store.updateSettings(
        (settings) =>
            settings.withFavorites(settings.favorites.remove(serverId)),
      );

  @override
  Future<MultiplayerSettings> recordJoin({
    required String serverId,
    required String name,
  }) {
    final bookmark = _bookmark(serverId, name);
    return _store.updateSettings(
      (settings) => settings.withHistory(settings.history.record(bookmark)),
    );
  }

  @override
  Future<MultiplayerSettings> removeFromHistory(String serverId) =>
      _store.updateSettings(
        (settings) => settings.withHistory(settings.history.remove(serverId)),
      );

  @override
  Future<MultiplayerSettings> clearHistory() => _store.updateSettings(
    (settings) => settings.withHistory(settings.history.clear()),
  );

  ServerBookmark _bookmark(String serverId, String name) =>
      ServerBookmark(serverId: serverId, name: name, savedAt: _clock());

  @override
  LanBrowseSnapshot get lanSnapshot => _lan.snapshot;

  @override
  Stream<LanBrowseSnapshot> get lanSnapshots => _lan.snapshots;

  @override
  Future<LanBrowseSnapshot> startLanBrowsing() async {
    final settings = (await _store.loadSettings()).settings;
    if (!settings.lanDiscoveryEnabled) return _lan.stop(disabled: true);
    return _lan.start();
  }

  @override
  Future<LanBrowseSnapshot> stopLanBrowsing() => _lan.stop();

  @override
  Future<InternetBrowseResult> browseInternet({
    String? after,
    CancellationToken? cancellation,
  }) => _controlPlane.browse(after: after, cancellation: cancellation);

  @override
  Future<JoinEvent?> answerJoin(
    JoinSessionState state, {
    CancellationToken? cancellation,
  }) async => switch (state.stage) {
    JoinStage.resolving => await resolveTarget(
      state.target,
      cancellation: cancellation,
    ),
    JoinStage.fetchingDescriptor => await fetchDescriptor(
      state.descriptorRequest!,
      cancellation: cancellation,
    ),
    _ => null,
  };

  @override
  Future<JoinEvent?> resolveTarget(
    JoinTarget target, {
    CancellationToken? cancellation,
  }) {
    if (!target.resolvesThroughService) {
      throw ArgumentError(
        'Only invites, server links and Internet listings are resolved.',
        'target',
      );
    }
    return _controlPlane.resolve(target, cancellation: cancellation);
  }

  @override
  Future<JoinEvent?> fetchDescriptor(
    JoinDescriptorRequest request, {
    CancellationToken? cancellation,
  }) => _resolver.answer(request, cancellation: cancellation);

  @override
  Future<MultiplayerNetworkReport> checkNetwork({
    bool checkPorts = false,
    bool developerMode = false,
  }) => _diagnostics.run(
    service: serviceStatus,
    lan: _lan.snapshot,
    checkPorts: checkPorts,
    developerMode: developerMode,
  );

  @override
  Future<void> dispose() => _lan.dispose();
}
