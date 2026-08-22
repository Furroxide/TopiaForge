/// Opt-in reachability probe service: settings persistence, policy enforcement, and one local run.
///
/// The probe measures how many real TopiaForge hosts are directly reachable. That number, `P(host reachable)`, is the
/// dominant term in the multiplayer relay cost model and has never been measured — see
/// `docs/internal/LauncherReachabilityProbe.md` and `docs/internal/MultiplayerTransportOptions.md`.
///
/// It touches neither Robotopia nor the multiplayer contract, and does not require the hosting feasibility gate to
/// be closed: it measures the launcher's own host, which is the host a session would run on anyway.
library;

import 'dart:convert';
import 'dart:io';

import 'package:launcher_domain/launcher_domain.dart';
import 'package:path/path.dart' as p;

import 'reachability/reachability_probe_runner.dart';
import 'reachability/stun_transport.dart';

/// Runs the probe subject to the policy gates, and persists the player's opt-in.
///
/// Nothing this class produces leaves the machine. There is deliberately no reporting transport: the aggregate
/// report shape exists in `launcher_domain`, but no code sends it, because
/// [ReachabilityProbePolicy.reportingApproved] is `false` and an approved privacy notice is a release blocker.
class ReachabilityProbeService implements ReachabilityProbeGateway {
  ReachabilityProbeService({
    required String dataRoot,
    ReachabilityProbeRunner runner = const ReachabilityProbeRunner(),
    ReachabilityClassifier classifier = const ReachabilityClassifier(),
    ReachabilityProbePolicy policy = const ReachabilityProbePolicy(),
    StunServerList serverList = const StunServerList(),
    List<String> configuredServers = const [],
    StunTransportFactory? openTransport,
  }) : _dataRoot = dataRoot,
       _configuredServers = configuredServers,
       _runner = runner,
       _classifier = classifier,
       _policy = policy,
       _serverList = serverList,
       _openTransport = openTransport ?? UdpStunTransport.bind;

  /// Where the opt-in is stored, relative to the launcher data root.
  static const settingsFileName = 'reachability-probe.json';

  final String _dataRoot;
  final ReachabilityProbeRunner _runner;
  final ReachabilityClassifier _classifier;
  final ReachabilityProbePolicy _policy;
  final StunServerList _serverList;
  final List<String> _configuredServers;
  final StunTransportFactory _openTransport;

  File get _settingsFile => File(p.join(_dataRoot, settingsFileName));

  /// Loads the opt-in, defaulting to off.
  ///
  /// A missing, unreadable, or malformed file resolves to the default rather than throwing. The default is off, so
  /// every failure mode fails closed.
  @override
  Future<ReachabilityProbeSettings> loadSettings() async {
    try {
      final file = _settingsFile;
      if (!await file.exists()) return const ReachabilityProbeSettings();
      final decoded = jsonDecode(await file.readAsString());
      if (decoded is! Map<String, Object?>) {
        return const ReachabilityProbeSettings();
      }
      return ReachabilityProbeSettings.fromJson(decoded);
    } on FormatException {
      return const ReachabilityProbeSettings();
    } on FileSystemException {
      return const ReachabilityProbeSettings();
    }
  }

  @override
  Future<void> saveSettings(ReachabilityProbeSettings settings) async {
    final file = _settingsFile;
    await file.parent.create(recursive: true);
    await file.writeAsString(
      '${const JsonEncoder.withIndent('  ').convert(settings.toJson())}\n',
      flush: true,
    );
  }

  /// Runs the probe once, locally.
  ///
  /// [servers] are `address:port` entries for STUN servers that advertise an alternate address. There is no default
  /// list: the probe contacts nothing unless a maintainer configures where to send it.
  ///
  /// Every entry must name the same address family. Mapping is decided by comparing reflexive endpoints across the
  /// server's addresses and ports, and endpoints in different families are not comparable, so a mixed list is a
  /// misconfiguration rather than a fallback list; it is refused instead of silently reduced to one family.
  @override
  Future<ReachabilityProbeOutcome> run({
    required bool developerMode,
    List<String>? servers,
  }) async {
    final settings = await loadSettings();
    final refusal = _policy.refuseRun(
      developerMode: developerMode,
      settings: settings,
    );
    if (refusal != null) return ReachabilityProbeOutcome.refused(refusal);

    final endpoints = _serverList.parse(servers ?? _configuredServers);
    if (endpoints.isEmpty) {
      return const ReachabilityProbeOutcome.unavailable(
        'No probe servers are configured. Add at least one address:port entry.',
      );
    }

    final wantsIPv4 = endpoints.first.isIPv4;
    if (endpoints.any((endpoint) => endpoint.isIPv4 != wantsIPv4)) {
      return const ReachabilityProbeOutcome.unavailable(
        'Probe servers must all use the same address family. One run compares reflexive '
        'endpoints across a server address and port, and endpoints in different families '
        'are not comparable.',
      );
    }

    StunTransport? transport;
    try {
      transport = await _openTransport(
        wantsIPv4 ? InternetAddressType.IPv4 : InternetAddressType.IPv6,
      );
      final observation = await _runner.run(transport, endpoints);
      return ReachabilityProbeOutcome.completed(
        _classifier.classify(observation),
      );
    } on SocketException catch (error) {
      return ReachabilityProbeOutcome.unavailable(
        'Could not open a UDP socket for the probe: ${error.message}',
      );
    } finally {
      await transport?.close();
    }
  }

  /// Builds the aggregate payload that *would* be reported, for review only.
  ///
  /// Returns the refusal instead when reporting is not permitted, which in this build it never is.
  Object reportPreview({
    required bool developerMode,
    required ReachabilityProbeSettings settings,
    required NatClassification classification,
  }) {
    final refusal = _policy.refuseReport(
      developerMode: developerMode,
      settings: settings,
    );
    if (refusal != null) return refusal;
    return ReachabilityReport(classification: classification).toJson();
  }
}
