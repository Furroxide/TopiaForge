/// The launcher's multiplayer facade over its network clients and store.
library;

import 'dart:async';

import 'package:launcher_domain/launcher_domain.dart';
import 'package:path/path.dart' as p;

import 'control_plane.dart';
import 'descriptor_client.dart';
import 'join_descriptor_resolver.dart';
import 'lan_discovery_client.dart';
import 'multiplayer_preferences_keeper.dart';
import 'multiplayer_store.dart';
import 'network_diagnostics.dart';

/// What the launcher's bloc asks of multiplayer: the player's settings,
/// favorites and history; their preferences and the game's copy of them
/// ([MultiplayerGateway]); LAN and Internet browsing; the join stages these
/// clients answer; and network diagnostics.
///
/// Nothing it returns names an address. Listings, bookmarks, events and
/// reports name servers by server id; where a LAN server was heard stays in
/// the LAN client, and a typed address stays in the join target the player
/// typed.
///
/// Every change to the settings, favorites or history throws
/// [MultiplayerStoreException] when it cannot be written, and while the
/// stored settings cannot be read, which are then left as they are.
abstract interface class MultiplayerRepository implements MultiplayerGateway {
  /// Reads the settings, recovering from damage. Never throws. While they
  /// cannot be read, [MultiplayerStore.unreadableSettings] stand in.
  Future<MultiplayerSettingsLoad> loadSettings();

  /// Saves [settings]. Turning LAN discovery off stops browsing at once,
  /// even when the save then fails, and a LAN start called after this reads
  /// these settings, even before they are written.
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

  /// What the multiplayer service offers right now, read afresh: which
  /// features are on, whether the relay is available and why not, so the
  /// launcher can degrade before the player hosts or joins. Throws
  /// [OperationCancelledException] when [cancellation] stops it, and nothing
  /// else.
  Future<MultiplayerServiceCheck> readServiceStatus({
    CancellationToken? cancellation,
  });

  /// A page of the Internet server browser for [filter], after the server id
  /// a page named as its `next`, or why it is unavailable. Throws
  /// [OperationCancelledException] when [cancellation] stops it, and nothing
  /// else.
  Future<InternetBrowseResult> browseInternet(
    ServerBrowseFilter filter, {
    String? after,
    CancellationToken? cancellation,
  });

  /// One public server with its descriptor verified, for a browser's detail
  /// pane, or why there is none. Throws [OperationCancelledException] when
  /// [cancellation] stops it, and nothing else.
  Future<InternetServerLookup> fetchInternetServer(
    String serverId, {
    CancellationToken? cancellation,
  });

  /// Answers [state] when it waits on the launcher, so every event
  /// [JoinSession.reduce] takes comes from one place:
  ///
  /// - resolving a target through the service, and fetching its descriptor;
  /// - with a [MultiplayerContentHost], downloading, installing into
  ///   [install], and preparing the server profile. Download and install
  ///   progress go to [onProgress] as the events the reducer takes for them,
  ///   [JoinDownloadProgressed] and [JoinInstallProgressed]. Packages only
  ///   the host serves download from the host as the descriptor fetch
  ///   reached it (`JoinHostPackages`), and a LAN host's browsing entry
  ///   stays alive until the downloading stage ends.
  ///
  /// Null for any other stage, for a content stage when there is no content
  /// host, and when [cancellation] stopped the work. The content stages need
  /// [install]; without one they throw [ArgumentError].
  Future<JoinEvent?> answerJoin(
    JoinSessionState state, {
    CancellationToken? cancellation,
    GameInstall? install,
    void Function(JoinEvent progress)? onProgress,
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
}

/// [MultiplayerRepository] over the launcher's data folder and real sockets.
///
/// The store lives in `<dataRoot>/multiplayer/`; pass [store] so everything
/// in the launcher that uses the folder shares one, and with it one lane of
/// reads and writes. The service is whatever this build was compiled with:
/// a build that names one talks to it over HTTPS ([openControlPlane]), and a
/// build without one contacts no service at all ([UnavailableControlPlane]).
/// [content] answers the stages that need the game's files; the launcher
/// passes its own launcher repository, which is a [MultiplayerContentHost].
/// [gameProbe] tells whether the game runs; tests pass their own.
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
    MultiplayerContentHost? content,
    GameProcessProbe? gameProbe,
  }) : this._(
         service ?? MultiplayerServiceConfiguration.fromEnvironment(),
         controlPlane,
         store ?? MultiplayerStore(p.join(dataRoot, folderName), clock: clock),
         descriptors ?? DescriptorClient(),
         lan,
         lookup,
         diagnostics ?? MultiplayerNetworkDiagnostics(),
         clock ?? DateTime.now,
         content,
         gameProbe,
       );

  LocalMultiplayerRepository._(
    MultiplayerServiceConfiguration service,
    ControlPlaneClient? controlPlane,
    this._store,
    DescriptorClient descriptors,
    LanDiscoveryClient? lan,
    HostLookup? lookup,
    this._diagnostics,
    this._clock,
    this._content,
    GameProcessProbe? gameProbe,
  ) : _controlPlane = controlPlane ?? openControlPlane(service),
      _lan = lan ?? LanDiscoveryClient(descriptors: descriptors),
      _preferences = MultiplayerPreferencesKeeper(
        _store,
        serviceOrigin: service.origin?.toString(),
        probe: gameProbe,
      ) {
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
  final MultiplayerContentHost? _content;
  final MultiplayerPreferencesKeeper _preferences;
  late final JoinDescriptorResolver _resolver;

  /// Counts the calls that decide whether LAN browsing runs, so a start still
  /// reading the settings yields to a later start, stop or discovery-off save.
  var _lanDecisions = 0;

  @override
  MultiplayerServiceStatus get serviceStatus => _controlPlane.status;

  @override
  Future<MultiplayerSettingsLoad> loadSettings() => _store.loadSettings();

  @override
  Future<MultiplayerSettings> saveSettings(MultiplayerSettings settings) {
    // Queued before anything else runs, so a start called after this reads
    // these settings, never the ones they replace.
    final saving = _store.saveSettings(settings);
    if (!settings.lanDiscoveryEnabled) {
      _lanDecisions++;
      // Stopping takes effect before this returns, even when the save fails.
      unawaited(_lan.stop(disabled: true));
    }
    return saving;
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
  Future<MultiplayerPreferences> loadPreferences() => _preferences.load();

  @override
  Future<MultiplayerPreferences> changePreferences(
    MultiplayerPreferencesChange change,
  ) {
    // Queued before anything else runs, as a save is, so a LAN start called
    // after this reads the change.
    final changing = _preferences.change(change);
    if (change.lanDiscovery == false) {
      _lanDecisions++;
      // Stopping takes effect before this returns, even when the save fails.
      unawaited(_lan.stop(disabled: true));
    }
    return changing;
  }

  @override
  Future<MultiplayerPreferencesRound> syncWithGame(
    GameInstall install, {
    CancellationToken? cancellation,
  }) async {
    final round = await _preferences.sync(install, cancellation: cancellation);
    final preferences = round.preferences;
    // A round can take in LAN discovery turned off in game. Once it is in the
    // settings, browsing stops, as it does when the player turns it off here.
    if (preferences.readable && !preferences.settings.lanDiscoveryEnabled) {
      _lanDecisions++;
      await _lan.stop(disabled: true);
    }
    return round;
  }

  @override
  Future<GameRunState> gameRunState(GameInstall install) =>
      _preferences.gameRunState(install);

  @override
  LanBrowseSnapshot get lanSnapshot => _lan.snapshot;

  @override
  Stream<LanBrowseSnapshot> get lanSnapshots => _lan.snapshots;

  @override
  Future<LanBrowseSnapshot> startLanBrowsing() async {
    final decision = ++_lanDecisions;
    final load = await _store.loadSettings();
    // A later start, stop or discovery-off save decides instead: the settings
    // read here may be older than it.
    if (decision != _lanDecisions) return _lan.snapshot;
    // Settings that cannot be read never count as the defaults, which would
    // turn discovery on for a player who turned it off.
    if (load.recovery == MultiplayerStoreRecovery.unreadable) {
      return _lan.stop(unavailable: LanDiscoveryProblem.settingsUnreadable);
    }
    if (!load.settings.lanDiscoveryEnabled) return _lan.stop(disabled: true);
    return _lan.start();
  }

  @override
  Future<LanBrowseSnapshot> stopLanBrowsing() {
    _lanDecisions++;
    return _lan.stop();
  }

  @override
  Future<MultiplayerServiceCheck> readServiceStatus({
    CancellationToken? cancellation,
  }) => _controlPlane.readStatus(cancellation: cancellation);

  @override
  Future<InternetBrowseResult> browseInternet(
    ServerBrowseFilter filter, {
    String? after,
    CancellationToken? cancellation,
  }) => _controlPlane.browse(filter, after: after, cancellation: cancellation);

  @override
  Future<InternetServerLookup> fetchInternetServer(
    String serverId, {
    CancellationToken? cancellation,
  }) => _controlPlane.fetchServer(serverId, cancellation: cancellation);

  @override
  Future<JoinEvent?> answerJoin(
    JoinSessionState state, {
    CancellationToken? cancellation,
    GameInstall? install,
    void Function(JoinEvent progress)? onProgress,
  }) async {
    GameInstall requireInstall() =>
        install ??
        (throw ArgumentError(
          'The ${state.stage.name} stage needs the game install.',
          'install',
        ));
    final content = _content;
    return switch (state.stage) {
      JoinStage.resolving => await resolveTarget(
        state.target,
        cancellation: cancellation,
      ),
      JoinStage.fetchingDescriptor => await fetchDescriptor(
        state.descriptorRequest!,
        cancellation: cancellation,
      ),
      JoinStage.downloading when content != null => await _download(
        content,
        requireInstall(),
        state,
        onProgress,
        cancellation,
      ),
      JoinStage.installing when content != null =>
        await content.installJoinContent(
          requireInstall(),
          state,
          onProgress: onProgress,
          cancellation: cancellation,
        ),
      JoinStage.preparingProfile when content != null =>
        await content.prepareJoinProfile(requireInstall(), state),
      _ => null,
    };
  }

  /// Answers the downloading stage through [content], with the packages
  /// only the host serves taken from the host as the descriptor fetch
  /// reached it (`JoinHostPackages`). A LAN host stays heard until the
  /// stage ends, however it ends: a stop asked for meanwhile waits for it,
  /// and browsing it had to start stops after it.
  Future<JoinEvent?> _download(
    MultiplayerContentHost content,
    GameInstall install,
    JoinSessionState state,
    void Function(JoinEvent progress)? onProgress,
    CancellationToken? cancellation,
  ) async {
    final request = state.downloadRequest;
    final host = request == null || request.hostDownloads.isEmpty
        ? null
        : _resolver.hostPackagesFor(request, mayBrowse: _lanAllowed);
    try {
      return await content.downloadJoinContent(
        install,
        state,
        onProgress: onProgress,
        cancellation: cancellation,
        hostPackages: host,
      );
    } finally {
      host?.release();
    }
  }

  /// Whether the stored settings let LAN browsing run: readable, with
  /// discovery on, as [startLanBrowsing] reads them.
  Future<bool> _lanAllowed() async {
    final load = await _store.loadSettings();
    return load.recovery != MultiplayerStoreRecovery.unreadable &&
        load.settings.lanDiscoveryEnabled;
  }

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
    int? gamePort,
    bool developerMode = false,
    CancellationToken? cancellation,
  }) => _diagnostics.run(
    service: serviceStatus,
    lan: _lan.snapshot,
    checkPorts: checkPorts,
    gamePort: gamePort,
    developerMode: developerMode,
    cancellation: cancellation,
  );

  @override
  Future<void> dispose() => _lan.dispose();
}
