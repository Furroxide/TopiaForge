part of '../launch_resolution.dart';

/// Where a session stands, in progress V2's `session.state`.
enum LaunchSessionState {
  /// The game has not started the session yet. Both roles.
  pending,

  /// A host binds its port and signs its descriptor.
  starting,
  hosting,
  stopping,

  /// The host stopped hosting and plays on alone.
  stopped,

  /// A join finds its server, checks it and asks for any password typed in
  /// game.
  resolving,
  checking,
  awaitingPassword,
  preparingProof,
  connecting,
  synchronizing,
  inSession,
  reconnecting,
  ended;

  String get wireName => _kebab(this);

  bool allows(LaunchSessionRole role) =>
      this == pending ||
      (role == LaunchSessionRole.host) ==
          (index >= starting.index && index <= stopped.index);
}

/// Why a session launch failed, in outcome V2's `multiplayer.failure`.
///
/// A join's failures are the launcher's [JoinFailureCode]s of the same name
/// that the connecting and synchronizing stages accept, so every one moves
/// the join reducer ([joinFailureCode]). The game folds its own causes into
/// them and explains the rest in `detail` and the host's admission report.
enum LaunchMultiplayerFailure {
  timeout,
  natTraversalFailed,
  relayUnavailable,
  serverFull,
  passwordRejected,
  kicked,
  protocolMismatch,
  admissionChanged,
  disconnected,
  directNotAllowed,
  snapshotFailed,
  worldFollowFailed,

  /// The host could not bind its port.
  portInUse,

  /// The host's multiplayer identity could not be read or created.
  identityUnavailable,

  /// The host's network stopped after too many failures in a row.
  networkStopped,

  /// The control plane would not list or register the session.
  serviceUnavailable;

  String get wireName => _kebab(this);

  bool allows(LaunchSessionRole role) =>
      (role == LaunchSessionRole.host) == (index >= portInUse.index);

  /// The join reducer's failure, for a join's failures; null for a host's.
  JoinFailureCode? get joinFailureCode => switch (this) {
    timeout => JoinFailureCode.timeout,
    natTraversalFailed => JoinFailureCode.natTraversalFailed,
    relayUnavailable => JoinFailureCode.relayUnavailable,
    serverFull => JoinFailureCode.serverFull,
    passwordRejected => JoinFailureCode.passwordRejected,
    kicked => JoinFailureCode.kicked,
    protocolMismatch => JoinFailureCode.protocolMismatch,
    admissionChanged => JoinFailureCode.admissionChanged,
    disconnected => JoinFailureCode.disconnected,
    directNotAllowed => JoinFailureCode.directNotAllowed,
    snapshotFailed => JoinFailureCode.snapshotFailed,
    worldFollowFailed => JoinFailureCode.worldFollowFailed,
    portInUse ||
    identityUnavailable ||
    networkStopped ||
    serviceUnavailable => null,
  };
}

/// Why a session that began has ended, in outcome V2's
/// `multiplayer.endReason`, mirroring the game's
/// `MultiplayerSessionEndReason`.
enum LaunchSessionEndReason {
  left,
  hostEnded,
  kicked,
  banned,
  timedOut,
  replaced,
  admissionRejected,
  protocolMismatch,
  transportFailed,
  worldFollowFailed;

  String get wireName => _kebab(this);

  JoinSessionEndReason get joinEndReason =>
      JoinSessionEndReason.values.byName(name);
}

/// The code of a host's admission report (`topiaforge.admission-report/1`).
enum LaunchAdmissionCode {
  other,
  full,
  protocol,
  passwordRequired,
  passwordWrong,
  tooManyAttempts,
  banned,
  content;

  String get wireName => _kebab(this);
}

/// The host's admission report for a refused join: its code, and up to 64
/// problems of display text, each at most 256 UTF-16 code units. It never
/// names an address.
final class LaunchAdmissionReport {
  LaunchAdmissionReport({
    required this.code,
    Iterable<String> problems = const [],
  }) : problems = List.unmodifiable([
         for (final problem in problems.take(65)) _displayText(problem, 256),
       ]) {
    if (this.problems.length > 64) {
      throw const FormatException('An admission report has 64 problems.');
    }
  }

  factory LaunchAdmissionReport.fromJson(Object? value) {
    final json = _object(value, {'code', 'problems'}, {'code', 'problems'});
    return LaunchAdmissionReport(
      code: _named(
        json['code'],
        LaunchAdmissionCode.values,
        (item) => item.wireName,
      ),
      problems: _list(
        json['problems'],
        (item) => _displayText(item, 256),
        max: 64,
      ),
    );
  }

  final LaunchAdmissionCode code;
  final List<String> problems;

  Map<String, Object?> toJson() => {
    'code': code.wireName,
    'problems': problems,
  };
}

/// Progress V2's `session` block: the session a V5 launch hosts or joins. It
/// names no address and no port.
final class LaunchProgressSession {
  LaunchProgressSession({
    required this.role,
    required String intentId,
    required this.state,
    String? serverId,
    int? players,
    int? maxPlayers,
    String? inviteCode,
  }) : intentId = _token(intentId),
       serverId = serverId == null ? null : _sha256Hex(serverId),
       maxPlayers = maxPlayers == null
           ? null
           : _ranged(
               maxPlayers,
               ServerDescriptorV1.minimumPlayers,
               ServerDescriptorV1.maximumPlayers,
             ),
       players = players == null
           ? null
           : _ranged(players, 0, ServerDescriptorV1.maximumPlayers),
       inviteCode = inviteCode == null ? null : _inviteCode(inviteCode) {
    if (!state.allows(role) ||
        (this.players == null) != (this.maxPlayers == null) ||
        (this.players != null && this.players! > this.maxPlayers!) ||
        (this.inviteCode != null && role != LaunchSessionRole.host)) {
      throw const FormatException('Inconsistent session progress.');
    }
  }

  factory LaunchProgressSession.fromJson(Object? value) {
    final json = _object(
      value,
      {
        'intentId',
        'inviteCode',
        'maxPlayers',
        'players',
        'role',
        'serverId',
        'state',
      },
      {'intentId', 'role', 'state'},
    );
    return LaunchProgressSession(
      role: _named(json['role'], LaunchSessionRole.values, (item) => item.name),
      intentId: _token(json['intentId']),
      state: _named(
        json['state'],
        LaunchSessionState.values,
        (item) => item.wireName,
      ),
      serverId: json.containsKey('serverId')
          ? _sha256Hex(json['serverId'])
          : null,
      players: json.containsKey('players') ? _integer(json['players']) : null,
      maxPlayers: json.containsKey('maxPlayers')
          ? _integer(json['maxPlayers'])
          : null,
      inviteCode: json.containsKey('inviteCode')
          ? _inviteCode(json['inviteCode'])
          : null,
    );
  }

  final LaunchSessionRole role;
  final String intentId;
  final LaunchSessionState state;
  final String? serverId;
  final int? players;
  final int? maxPlayers;

  /// A host's invite code, once the control plane issued one.
  final String? inviteCode;

  Map<String, Object?> toJson() => {
    'intentId': intentId,
    if (inviteCode != null) 'inviteCode': inviteCode,
    if (maxPlayers != null) 'maxPlayers': maxPlayers,
    if (players != null) 'players': players,
    'role': role.name,
    if (serverId != null) 'serverId': serverId,
    'state': state.wireName,
  };
}

/// Outcome V2's `multiplayer` block: how a session launch went, or how its
/// session ended. It names no address and no port.
final class LaunchOutcomeMultiplayer {
  LaunchOutcomeMultiplayer({
    required this.role,
    required String intentId,
    String? serverId,
    this.failure,
    String? detail,
    this.admission,
    this.endReason,
  }) : intentId = _token(intentId),
       serverId = serverId == null ? null : _sha256Hex(serverId),
       detail = detail == null ? null : _displayText(detail, 1024) {
    if ((failure != null && !failure!.allows(role)) ||
        (admission != null && role != LaunchSessionRole.join)) {
      throw const FormatException('Inconsistent session outcome.');
    }
  }

  factory LaunchOutcomeMultiplayer.fromJson(Object? value) {
    final json = _object(
      value,
      {
        'admission',
        'detail',
        'endReason',
        'failure',
        'intentId',
        'role',
        'serverId',
      },
      {'intentId', 'role'},
    );
    return LaunchOutcomeMultiplayer(
      role: _named(json['role'], LaunchSessionRole.values, (item) => item.name),
      intentId: _token(json['intentId']),
      serverId: json.containsKey('serverId')
          ? _sha256Hex(json['serverId'])
          : null,
      failure: json.containsKey('failure')
          ? _named(
              json['failure'],
              LaunchMultiplayerFailure.values,
              (item) => item.wireName,
            )
          : null,
      detail: json.containsKey('detail')
          ? _displayText(json['detail'], 1024)
          : null,
      admission: json.containsKey('admission')
          ? LaunchAdmissionReport.fromJson(json['admission'])
          : null,
      endReason: json.containsKey('endReason')
          ? _named(
              json['endReason'],
              LaunchSessionEndReason.values,
              (item) => item.wireName,
            )
          : null,
    );
  }

  final LaunchSessionRole role;
  final String intentId;
  final String? serverId;
  final LaunchMultiplayerFailure? failure;

  /// A sentence for the player, never naming an address.
  final String? detail;
  final LaunchAdmissionReport? admission;
  final LaunchSessionEndReason? endReason;

  Map<String, Object?> toJson() => {
    if (admission != null) 'admission': admission!.toJson(),
    if (detail != null) 'detail': detail,
    if (endReason != null) 'endReason': endReason!.wireName,
    if (failure != null) 'failure': failure!.wireName,
    'intentId': intentId,
    'role': role.name,
    if (serverId != null) 'serverId': serverId,
  };
}
