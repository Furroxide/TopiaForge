part of '../launch_resolution.dart';

const _launchPhases = [
  'idle',
  'preparing',
  'loading-world',
  'starting-mode',
  'running',
  'stopping',
];

final class LaunchProgress {
  LaunchProgress({
    required String requestId,
    String? sessionId,
    required int sequence,
    required String phase,
    this.nativeBusy,
  }) : requestId = _token(requestId),
       sessionId = sessionId == null ? null : _token(sessionId),
       sequence = _integer(sequence),
       phase = _choice(phase, _launchPhases);
  factory LaunchProgress.fromJson(Object? value) {
    final json = _object(
      value,
      {
        'schemaVersion',
        'requestId',
        'sessionId',
        'sequence',
        'phase',
        'nativeBusy',
      },
      {'schemaVersion', 'requestId', 'sequence', 'phase'},
    );
    if (_integer(json['schemaVersion']) != 1) {
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
    );
  }
  final String requestId;
  final String? sessionId;
  final int sequence;
  final String phase;
  final bool? nativeBusy;
  Map<String, Object?> toJson() => {
    'schemaVersion': 1,
    'requestId': requestId,
    if (sessionId != null) 'sessionId': sessionId,
    'sequence': sequence,
    'phase': phase,
    if (nativeBusy != null) 'nativeBusy': nativeBusy,
  };
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
  }) : kind = _choice(kind, const ['launch', 'session']),
       requestId = _token(requestId),
       command = command == null
           ? null
           : _choice(command, const ['main-menu', 'launch-target']),
       sessionId = sessionId == null ? null : _token(sessionId),
       sequence = _integer(sequence),
       status = _choice(status, const ['succeeded', 'failed', 'cancelled']),
       phase = _choice(phase, _launchPhases),
       blocks = _orderedBlocks(_boundedCopy(blocks)) {
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
      },
      {
        'schemaVersion',
        'kind',
        'requestId',
        'sequence',
        'status',
        'phase',
        'blocks',
      },
    );
    if (_integer(json['schemaVersion']) != 1) {
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
  Map<String, Object?> toJson() => {
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
