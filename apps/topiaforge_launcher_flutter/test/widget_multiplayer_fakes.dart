part of 'widget_test.dart';

/// The link-handling choice held in memory, as a store that always reads.
final class _MemorySessionLinkSetting implements SessionLinkSetting {
  bool _enabled = true;
  final _changes = StreamController<bool>.broadcast();

  @override
  Future<bool> read() async => _enabled;

  @override
  Stream<bool> get changes => _changes.stream;

  @override
  Future<void> write(bool enabled) async {
    if (enabled == _enabled) return;
    _enabled = enabled;
    _changes.add(enabled);
  }
}

/// An address planted in every failure the fake multiplayer gateway reports,
/// so a test sees at once if any failure's own text travels further.
const _plantedAddress = '203.0.113.77';
const _plantedEndpoint = '[2001:db8::77]:27250';

/// A short description of [change], for the calls a fake records.
String _describeChange(MultiplayerPreferencesChange change) => [
  if (change.displayName case final name?) 'name $name',
  if (change.acknowledgeDisclosure) 'disclosure',
  if (change.hostingPort case final port?) 'port $port',
  if (change.upnp case final upnp?) 'upnp $upnp',
  if (change.lanDiscovery case final lan?) 'lan $lan',
  if (change.voiceConsent case final consent?) 'consent $consent',
  if (change.voice case final voice?) 'voice $voice',
  if (change.acknowledgeNetworkNotice) 'notice',
].join(', ');

/// A report whose three port checks found [ports]; a game port of 0, any
/// free port, is never checked.
MultiplayerNetworkReport _networkReport({
  MultiplayerServiceStatus service = MultiplayerServiceStatus.unconfigured,
  PortCheckResult ports = PortCheckResult.notChecked,
  int gamePort = 27250,
  LanBrowseSnapshot? lan,
}) => MultiplayerNetworkReport(
  service: service,
  gamePortUdp: PortCheck(
    gamePort,
    PortProtocol.udp,
    gamePort == 0 ? PortCheckResult.notChecked : ports,
  ),
  gamePortTcp: PortCheck(
    gamePort,
    PortProtocol.tcp,
    gamePort == 0 ? PortCheckResult.notChecked : ports,
  ),
  lanPort: PortCheck(27251, PortProtocol.udp, ports),
  lan: lan ?? LanBrowseSnapshot.stopped,
  upnp: NetworkCheckState.notAvailable,
  turn: NetworkCheckState.skipped,
);

/// A scripted [MultiplayerGateway] that records every call. With [hold],
/// each read, game check, round and network check waits until the test
/// completes it, in any order, as slow disks and process lists make real
/// ones wait. Every failure it reports names an address in its own text.
final class _FakeMultiplayerGateway implements MultiplayerGateway {
  _FakeMultiplayerGateway({MultiplayerPreferences? preferences})
    : preferences =
          preferences ??
          MultiplayerPreferences(
            settings: MultiplayerSettings(displayName: 'Sam'),
          );

  MultiplayerPreferences preferences;
  final calls = <String>[];
  GameRunState running = GameRunState.notRunning;

  /// Reads and changes behave as a store that cannot be read just now.
  bool unreadable = false;

  /// Changes cannot be written.
  bool failSaves = false;

  /// Every call throws this, after its gate.
  Object? failure;

  /// What a round answers; a plain synced round when null.
  MultiplayerPreferencesRound Function(MultiplayerPreferences current)? round;

  /// Holds each answer until the test completes it.
  bool hold = false;
  final heldLoads = <Completer<MultiplayerPreferences>>[];
  final heldGames = <Completer<GameRunState>>[];
  final heldRounds = <Completer<MultiplayerPreferencesRound>>[];
  final heldChecks = <Completer<MultiplayerNetworkReport>>[];
  Completer<void>? changeGate;
  final roundTokens = <CancellationToken?>[];
  final checkTokens = <CancellationToken?>[];

  /// The game port each network check was asked about.
  final checkedGamePorts = <int?>[];
  MultiplayerNetworkReport? report;
  final _lan = StreamController<LanBrowseSnapshot>.broadcast();
  LanBrowseSnapshot lan = LanBrowseSnapshot.stopped;

  static MultiplayerPreferences standIns() => MultiplayerPreferences(
    settings: MultiplayerSettings(
      lanDiscoveryEnabled: false,
      linkHandlingEnabled: false,
    ),
    health: MultiplayerPreferencesHealth.unreadable,
  );

  Future<void> _after(Completer<void>? gate) async {
    await gate?.future;
    final failed = failure;
    if (failed != null) throw failed;
  }

  /// [answer] now, or once the test completes it when [hold] is on.
  Future<T> _held<T>(List<Completer<T>> held, Future<T> Function() answer) {
    if (!hold) return answer();
    final completer = Completer<T>();
    held.add(completer);
    return completer.future;
  }

  void emitLan(LanBrowseSnapshot snapshot) {
    lan = snapshot;
    if (!_lan.isClosed) _lan.add(snapshot);
  }

  @override
  Future<MultiplayerPreferences> loadPreferences() async {
    calls.add('load');
    return _held(heldLoads, () async {
      await _after(null);
      return unreadable ? standIns() : preferences;
    });
  }

  @override
  Future<MultiplayerPreferences> changePreferences(
    MultiplayerPreferencesChange change,
  ) async {
    calls.add('change ${_describeChange(change)}');
    if (change.lanDiscovery == false) {
      emitLan(LanBrowseSnapshot.disabled);
    }
    await _after(changeGate);
    if (unreadable) {
      throw const MultiplayerPreferencesException(
        'held by a program at $_plantedAddress',
        unreadable: true,
      );
    }
    if (failSaves) {
      throw const MultiplayerPreferencesException(
        'disk full while $_plantedEndpoint wrote',
      );
    }
    return preferences = change.applyTo(preferences);
  }

  @override
  Future<MultiplayerPreferencesRound> syncWithGame(
    GameInstall install, {
    CancellationToken? cancellation,
  }) async {
    calls.add('round');
    roundTokens.add(cancellation);
    return _held(heldRounds, () async {
      await _after(null);
      if (cancellation?.isCancelled ?? false) {
        return MultiplayerPreferencesRoundSkipped(preferences);
      }
      if (unreadable) return MultiplayerPreferencesRoundSkipped(standIns());
      final answer =
          round?.call(preferences) ??
          MultiplayerPreferencesSynced(preferences, changedInGame: false);
      if (answer is MultiplayerPreferencesSynced) {
        preferences = answer.preferences;
      }
      return answer;
    });
  }

  @override
  Future<GameRunState> gameRunState(GameInstall install) async {
    calls.add('game');
    return _held(heldGames, () async {
      await _after(null);
      return running;
    });
  }

  @override
  MultiplayerServiceStatus serviceStatus =
      MultiplayerServiceStatus.unconfigured;

  @override
  LanBrowseSnapshot get lanSnapshot => lan;

  @override
  Stream<LanBrowseSnapshot> get lanSnapshots => _lan.stream;

  @override
  Future<LanBrowseSnapshot> startLanBrowsing() async {
    calls.add('lan start');
    await _after(null);
    emitLan(
      LanBrowseSnapshot(
        status: preferences.settings.lanDiscoveryEnabled
            ? LanDiscoveryStatus.listening
            : LanDiscoveryStatus.disabled,
      ),
    );
    return lan;
  }

  @override
  Future<LanBrowseSnapshot> stopLanBrowsing() async {
    calls.add('lan stop');
    await _after(null);
    emitLan(LanBrowseSnapshot.stopped);
    return lan;
  }

  @override
  Future<MultiplayerNetworkReport> checkNetwork({
    bool checkPorts = false,
    int? gamePort,
    bool developerMode = false,
    CancellationToken? cancellation,
  }) async {
    calls.add(checkPorts ? 'check ports' : 'check');
    checkTokens.add(cancellation);
    checkedGamePorts.add(gamePort);
    return _held(heldChecks, () async {
      await _after(null);
      final port = gamePort ?? LanBeacon.defaultGamePort;
      return report ??
          _networkReport(
            service: serviceStatus,
            ports: checkPorts
                ? PortCheckResult.available
                : PortCheckResult.notChecked,
            gamePort: port,
            lan: lan,
          );
    });
  }

  @override
  Future<void> dispose() async {
    calls.add('dispose');
    await _lan.close();
  }
}
