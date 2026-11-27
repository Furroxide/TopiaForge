part of '../launch_resolution.dart';

const _launchPhases = [
  'idle',
  'preparing',
  'loading-world',
  'starting-mode',
  'running',
  'stopping',
];

/// Version 2 adds the phases a join passes before its session is playable.
const _sessionLaunchPhases = [..._launchPhases, 'connecting', 'synchronizing'];

/// Request-correlated progress. Version 1 reports a gamemode launch. Version
/// 2 reports a session launch (request V5): it carries a [session] block and
/// may be `connecting` or `synchronizing`, which only a join is. Version 1
/// is written exactly as before version 2 existed; version 2 is sorted JSON.
final class LaunchProgress {
  LaunchProgress({
    required String requestId,
    String? sessionId,
    required int sequence,
    required String phase,
    this.nativeBusy,
    this.session,
  }) : requestId = _token(requestId),
       sessionId = sessionId == null ? null : _token(sessionId),
       sequence = _integer(sequence),
       phase = _choice(
         phase,
         session == null ? _launchPhases : _sessionLaunchPhases,
       ) {
    if (session != null &&
        session!.role != LaunchSessionRole.join &&
        (this.phase == 'connecting' || this.phase == 'synchronizing')) {
      throw const FormatException('Only a join connects or synchronizes.');
    }
  }
  factory LaunchProgress.fromJson(Object? value) {
    final version = value is Map<String, Object?>
        ? _integer(value['schemaVersion'])
        : 0;
    final json = _object(
      value,
      {
        'schemaVersion',
        'requestId',
        'sessionId',
        'sequence',
        'phase',
        'nativeBusy',
        if (version == 2) 'session',
      },
      {
        'schemaVersion',
        'requestId',
        'sequence',
        'phase',
        if (version == 2) 'session',
      },
    );
    if (version != 1 && version != 2) {
      throw const FormatException('Unsupported progress version.');
    }
    return LaunchProgress(
      requestId: _token(json['requestId']),
      sessionId: json.containsKey('sessionId')
          ? _token(json['sessionId'])
          : null,
      sequence: _integer(json['sequence']),
      phase: _text(json['phase']),
      nativeBusy: json.containsKey('nativeBusy')
          ? _boolean(json['nativeBusy'])
          : null,
      session: version == 2
          ? LaunchProgressSession.fromJson(json['session'])
          : null,
    );
  }
  final String requestId;
  final String? sessionId;
  final int sequence;
  final String phase;
  final bool? nativeBusy;

  /// The session a V5 launch hosts or joins; present exactly in version 2.
  final LaunchProgressSession? session;

  int get schemaVersion => session == null ? 1 : 2;

  Map<String, Object?> toJson() {
    if (session != null) {
      return _sorted({
            if (nativeBusy != null) 'nativeBusy': nativeBusy,
            'phase': phase,
            'requestId': requestId,
            'schemaVersion': 2,
            'sequence': sequence,
            'session': session!.toJson(),
            if (sessionId != null) 'sessionId': sessionId,
          })!
          as Map<String, Object?>;
    }
    return {
      'schemaVersion': 1,
      'requestId': requestId,
      if (sessionId != null) 'sessionId': sessionId,
      'sequence': sequence,
      'phase': phase,
      if (nativeBusy != null) 'nativeBusy': nativeBusy,
    };
  }
}

final class LaunchOperationError {
  LaunchOperationError({required String code, required String message})
    : code = _choice(code, const [
        'invalidArgument',
        'notFound',
        'unavailable',
        'conflict',
        'invalidState',
        'cancelled',
        'timedOut',
        'io',
        'external',
        'unknown',
        'notAuthoritative',
        'rateLimited',
      ]),
      message = _text(message, max: 4096);
  factory LaunchOperationError.fromJson(Object? value) {
    final json = _object(value, {'code', 'message'}, {'code', 'message'});
    return LaunchOperationError(
      code: _text(json['code']),
      message: _text(json['message'], max: 4096),
    );
  }
  final String code;
  final String message;
  Map<String, Object?> toJson() => {'code': code, 'message': message};
}

/// Launch acknowledgement and terminal session outcome are separate records.
///
/// Version 1 reports a gamemode launch. Version 2 reports a session launch
/// (request V5) and carries a [multiplayer] block: a launch outcome succeeds
/// once hosting is open or the join is in session, and fails with the
/// failure and the host's admission report; a terminal outcome says why the
/// session ended. See `session_outputs.dart`.
final class LaunchOutcome {
  LaunchOutcome({
    required String kind,
    required String requestId,
    String? command,
    String? sessionId,
    required int sequence,
    required String status,
    required String phase,
    Iterable<LaunchBlock> blocks = const [],
    this.error,
    this.multiplayer,
  }) : kind = _choice(kind, const ['launch', 'session']),
       requestId = _token(requestId),
       command = command == null
           ? null
           : _choice(
               command,
               multiplayer == null
                   ? const ['main-menu', 'launch-target']
                   : sessionLaunchCommands,
             ),
       sessionId = sessionId == null ? null : _token(sessionId),
       sequence = _integer(sequence),
       status = _choice(status, const ['succeeded', 'failed', 'cancelled']),
       phase = _choice(
         phase,
         multiplayer == null ? _launchPhases : _sessionLaunchPhases,
       ),
       blocks = _orderedBlocks(_boundedCopy(blocks)) {
    final session = multiplayer;
    if (session != null) {
      _checkSessionOutcome(session);
      return;
    }
    if ((this.kind == 'launch' && this.command == null) ||
        (this.kind == 'session' &&
            (this.command != null ||
                this.sessionId == null ||
                this.phase != 'idle')) ||
        (this.status == 'succeeded' &&
            (this.blocks.isNotEmpty || error != null)) ||
        (this.status == 'failed' && this.blocks.isEmpty && error == null) ||
        (this.kind == 'launch' &&
            this.status == 'succeeded' &&
            (this.command == 'main-menu'
                ? this.phase != 'idle' || this.sessionId != null
                : this.phase != 'running' || this.sessionId == null))) {
      throw const FormatException('Inconsistent launch/session outcome.');
    }
  }
  factory LaunchOutcome.fromJson(Object? value) {
    final version = value is Map<String, Object?>
        ? _integer(value['schemaVersion'])
        : 0;
    final json = _object(
      value,
      {
        'schemaVersion',
        'kind',
        'requestId',
        'command',
        'sessionId',
        'sequence',
        'status',
        'phase',
        'blocks',
        'error',
        if (version == 2) 'multiplayer',
      },
      {
        'schemaVersion',
        'kind',
        'requestId',
        'sequence',
        'status',
        'phase',
        'blocks',
        if (version == 2) 'multiplayer',
      },
    );
    if (version != 1 && version != 2) {
      throw const FormatException('Unsupported outcome version.');
    }
    return LaunchOutcome(
      kind: _text(json['kind']),
      requestId: _token(json['requestId']),
      command: json.containsKey('command') ? _text(json['command']) : null,
      sessionId: json.containsKey('sessionId')
          ? _token(json['sessionId'])
          : null,
      sequence: _integer(json['sequence']),
      status: _text(json['status']),
      phase: _text(json['phase']),
      blocks: _list(json['blocks'], LaunchBlock.fromJson),
      error: json.containsKey('error')
          ? LaunchOperationError.fromJson(json['error'])
          : null,
      multiplayer: version == 2
          ? LaunchOutcomeMultiplayer.fromJson(json['multiplayer'])
          : null,
    );
  }
  final String kind;
  final String requestId;
  final String? command;
  final String? sessionId;
  final int sequence;
  final String status;
  final String phase;
  final List<LaunchBlock> blocks;
  final LaunchOperationError? error;

  /// How a session launch went; present exactly in version 2.
  final LaunchOutcomeMultiplayer? multiplayer;

  int get schemaVersion => multiplayer == null ? 1 : 2;

  /// Version 2's rules. A launch outcome names the role's command. It
  /// succeeds without failures at Running with a gamemode session or at
  /// Idle without one; it fails with a block, an error or a session failure,
  /// and only a failure carries the host's admission report. A terminal
  /// outcome is Idle, names no command and says why the session ended.
  void _checkSessionOutcome(LaunchOutcomeMultiplayer session) {
    const invalid = FormatException('Inconsistent session launch outcome.');
    final failed = session.failure != null;
    final ended = session.endReason;
    if (session.role != LaunchSessionRole.join &&
        (phase == 'connecting' || phase == 'synchronizing')) {
      throw invalid;
    }
    if (status == 'succeeded' &&
        (blocks.isNotEmpty || error != null || failed)) {
      throw invalid;
    }
    if (failed && status != 'failed') throw invalid;
    if (kind == 'launch') {
      if (command != session.role.command ||
          ended != null ||
          (session.admission != null && !failed) ||
          (status == 'failed' && blocks.isEmpty && error == null && !failed) ||
          (status == 'succeeded' &&
              (phase == 'idle'
                  ? sessionId != null
                  : phase != 'running' || sessionId == null))) {
        throw invalid;
      }
      return;
    }
    if (command != null ||
        phase != 'idle' ||
        ended == null ||
        failed ||
        (session.admission != null &&
            ended != LaunchSessionEndReason.admissionRejected) ||
        (status == 'failed' && blocks.isEmpty && error == null)) {
      throw invalid;
    }
  }

  Map<String, Object?> toJson() {
    if (multiplayer != null) {
      return _sorted({
            'blocks': blocks.map((item) => item.toJson()).toList(),
            if (command != null) 'command': command,
            if (error != null) 'error': error!.toJson(),
            'kind': kind,
            'multiplayer': multiplayer!.toJson(),
            'phase': phase,
            'requestId': requestId,
            'schemaVersion': 2,
            'sequence': sequence,
            if (sessionId != null) 'sessionId': sessionId,
            'status': status,
          })!
          as Map<String, Object?>;
    }
    return {
      'schemaVersion': 1,
      'kind': kind,
      'requestId': requestId,
      if (command != null) 'command': command,
      if (sessionId != null) 'sessionId': sessionId,
      'sequence': sequence,
      'status': status,
      'phase': phase,
      'blocks': blocks.map((item) => item.toJson()).toList(),
      if (error != null) 'error': error!.toJson(),
    };
  }
}
