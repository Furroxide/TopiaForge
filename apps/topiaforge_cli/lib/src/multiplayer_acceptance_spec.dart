import 'dart:convert';

/// Separate from the single-instance and release qualification inventories.
final class MultiplayerAcceptanceSpec {
  MultiplayerAcceptanceSpec._(this.cases);
  final List<MultiplayerAcceptanceCase> cases;
  static const packageId = 'dev.topiaforge.multiplayer-acceptance';
  static const topology = 'listen-pair';

  factory MultiplayerAcceptanceSpec.decode(String text) {
    try {
      final value = jsonDecode(text);
      if (value is! Map ||
          value['schemaVersion'] != 1 ||
          value['topology'] != topology ||
          value['packageId'] != packageId ||
          value['cases'] is! List) {
        throw const FormatException();
      }
      final rows = value['cases'] as List;
      if (rows.isEmpty || rows.length > 128) throw const FormatException();
      final ids = <String>{};
      final cases = <MultiplayerAcceptanceCase>[];
      for (final row in rows) {
        if (row is! Map ||
            row['id'] is! String ||
            !RegExp(
              r'^multiplayer\.[a-z][a-z0-9-]{0,80}$',
            ).hasMatch(row['id'] as String) ||
            !ids.add(row['id'] as String) ||
            !const ['automatic', 'manual'].contains(row['mode']) ||
            row['peers'] is! List ||
            row['steps'] is! String ||
            (row['steps'] as String).trim().isEmpty) {
          throw const FormatException();
        }
        final peers = row['peers'] as List;
        if (peers.isEmpty ||
            peers.length > 2 ||
            peers.toSet().length != peers.length ||
            peers.any((p) => p != 'host' && p != 'guest')) {
          throw const FormatException();
        }
        cases.add(
          MultiplayerAcceptanceCase(
            row['id'] as String,
            row['mode'] == 'manual',
            peers.cast<String>(),
            row['steps'] as String,
          ),
        );
      }
      return MultiplayerAcceptanceSpec._(List.unmodifiable(cases));
    } on Object {
      throw const FormatException(
        'TFMPACCEPT100: Invalid listen-pair specification. Update the suite and harness together.',
      );
    }
  }

  List<MultiplayerAcceptanceCase> select(List<String> ids) {
    if (ids.toSet().length != ids.length ||
        ids.any((id) => !cases.any((c) => c.id == id))) {
      throw const FormatException(
        'TFMPACCEPT100: Unknown or repeated case. Choose case ids from the multiplayer suite.',
      );
    }
    return ids.isEmpty
        ? cases
        : cases.where((c) => ids.contains(c.id)).toList();
  }

  Map<String, Object?> dryRun(List<String> ids) {
    final selected = select(ids);
    return {
      'schemaVersion': 1,
      'suite': 'multiplayer',
      'topology': topology,
      'dryRun': true,
      'liveEvidence': false,
      'packageId': packageId,
      'peers': ['host', 'guest'],
      'stages': [
        'validate-package',
        'admit-both-layouts',
        'install-both-peers',
        'challenge-both-peers',
        'launch-host',
        'confirm-host',
        'launch-guest',
        'confirm-guest',
        'collect-attributed-results',
        'stop-owned-peers',
        'verify-receipts',
      ],
      'cases': [for (final c in selected) c.toJson()],
      'requiredObservations': selected.fold<int>(
        0,
        (n, c) => n + c.peers.length,
      ),
    };
  }
}

final class MultiplayerAcceptanceCase {
  MultiplayerAcceptanceCase(
    this.id,
    this.manual,
    List<String> peers,
    this.steps,
  ) : peers = List.unmodifiable(peers);
  final String id, steps;
  final bool manual;
  bool get requiresService =>
      id == 'multiplayer.invite' || id == 'multiplayer.relay';
  final List<String> peers;
  Map<String, Object?> toJson() => {
    'id': id,
    'mode': manual ? 'manual' : 'automatic',
    'peers': peers,
    'steps': steps,
    if (requiresService) 'requiresService': true,
  };
}

/// No raw log detail enters the result. Each case needs its own per-peer,
/// challenge-bound marker from the acceptance package's attributed logger.
final class MultiplayerAcceptanceObservations {
  MultiplayerAcceptanceObservations(this.challenge, this.cases);
  final String challenge;
  final List<MultiplayerAcceptanceCase> cases;
  final _passed = <String>{};
  final _failed = <String>{};
  final _notRun = <String>{};
  final _ready = <String>{};

  void observe({
    required String peer,
    required String source,
    required String level,
    required String message,
  }) {
    if (source != MultiplayerAcceptanceSpec.packageId) return;
    if (level == 'INFO' &&
        message == 'TF-MP-SESSION|READY|$challenge|$peer' &&
        (peer == 'host' || peer == 'guest')) {
      _ready.add(peer);
    }
    final marker = RegExp(
      r'^TF-MP-ACCEPT\|(PASS|FAIL|NOT-RUN)\|([0-9a-f]{64})\|(host|guest)\|'
      r'(multiplayer\.[a-z][a-z0-9-]{0,80})\|(automatic|manual)$',
    ).firstMatch(message);
    if (marker == null || marker[2] != challenge || marker[3] != peer) return;
    final entry = cases.where((c) => c.id == marker[4]).firstOrNull;
    if (entry == null ||
        !entry.peers.contains(peer) ||
        (marker[5] == 'manual') != entry.manual) {
      return;
    }
    final key = '$peer:${entry.id}';
    if (marker[1] == 'PASS' && level == 'INFO') _passed.add(key);
    if (marker[1] == 'FAIL' && level == 'ERROR') _failed.add(key);
    if (marker[1] == 'NOT-RUN' && level == 'INFO') _notRun.add(key);
  }

  List<String> get missing => [
    for (final c in cases)
      for (final peer in c.peers)
        if (!_passed.contains('$peer:${c.id}')) '$peer:${c.id}',
  ];
  List<String> get failures => _failed.toList()..sort();
  List<String> get notRun => _notRun.toList()..sort();
  bool get bothInSession => _ready.containsAll(['host', 'guest']);
  bool get settled => bothInSession && missing.every(_notRun.contains);
  bool get complete =>
      bothInSession && missing.isEmpty && failures.isEmpty && notRun.isEmpty;
}
