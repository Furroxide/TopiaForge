part of 'control_plane_client.dart';

/// What the client keeps between calls, in memory only: what resolving a
/// join fetched, until that join's descriptor stage takes it, and which
/// invite codes missed lately, each by its SHA-256 rather than the code.
final class _Recall {
  _Recall(this._clock);

  static const _maximumResolved = 4;
  static const _maximumMisses = 64;

  final DateTime Function() _clock;
  final _resolved = <_Resolved>[];

  /// When each code that missed did, keyed by its SHA-256.
  final _misses = <String, DateTime>{};

  void keep(JoinTarget target, ServerAnswer answer) {
    _resolved
      ..removeWhere((entry) => identical(entry.target, target))
      ..add(_Resolved(target, answer, _clock()));
    while (_resolved.length > _maximumResolved) {
      _resolved.removeAt(0);
    }
  }

  /// What resolving [target] fetched for [serverId], taken once.
  ServerAnswer? take(JoinTarget target, String serverId) {
    final now = _clock();
    _resolved.removeWhere(
      (entry) =>
          now.difference(entry.at) > HttpsControlPlaneClient.resolvedMaxAge,
    );
    final index = _resolved.indexWhere(
      (entry) =>
          identical(entry.target, target) && entry.answer.serverId == serverId,
    );
    return index < 0 ? null : _resolved.removeAt(index).answer;
  }

  bool missedLately(String code) {
    final now = _clock();
    _misses.removeWhere(
      (_, at) => now.difference(at) >= HttpsControlPlaneClient.missMemory,
    );
    return _misses.containsKey(_keyOf(code));
  }

  void missed(String code) {
    final key = _keyOf(code);
    _misses
      ..remove(key)
      ..[key] = _clock();
    while (_misses.length > _maximumMisses) {
      _misses.remove(_misses.keys.first);
    }
  }

  static String _keyOf(String code) =>
      sha256.convert(utf8.encode(code)).toString();
}

final class _Resolved {
  _Resolved(this.target, this.answer, this.at);

  final JoinTarget target;
  final ServerAnswer answer;
  final DateTime at;
}

/// The canonical code of an invite target, or null for any other.
String? _inviteOf(JoinTarget target) => switch (target) {
  JoinLinkTarget(:final link) when link.kind == SessionLinkKind.invite =>
    link.inviteCode,
  _ => null,
};

/// Something the service could not give, in the launcher's own words.
final class _Failure {
  const _Failure(this.problem, this.message, {this.retryAfter});

  final MultiplayerServiceProblem problem;
  final String message;
  final Duration? retryAfter;
}

enum _Subject { status, servers, server, invite }

const _inviteMiss = _Failure(
  MultiplayerServiceProblem.notFound,
  'That invite code does not work: it may be mistyped, expired or '
  'withdrawn, or its session may have ended.',
);

const _unreadable = _Failure(
  MultiplayerServiceProblem.invalidAnswer,
  'The TopiaForge service sent an answer this launcher could not read. Try '
  'again shortly.',
);

InternetServerLookup _lookupFailed(_Failure failure) =>
    InternetServerLookup.failed(
      failure.problem,
      failure.message,
      retryAfter: failure.retryAfter,
    );

/// The join failure for [failure], among those the resolving and
/// descriptor stages can report.
JoinFailed _joinFailed(
  _Failure failure, {
  required bool invite,
}) => JoinFailed(switch (failure.problem) {
  MultiplayerServiceProblem.offline => JoinFailureCode.offline,
  MultiplayerServiceProblem.rateLimited => JoinFailureCode.rateLimited,
  MultiplayerServiceProblem.notFound =>
    invite ? JoinFailureCode.inviteExpired : JoinFailureCode.serverNotListed,
  MultiplayerServiceProblem.descriptorInvalid =>
    JoinFailureCode.descriptorInvalid,
  MultiplayerServiceProblem.notConfigured ||
  MultiplayerServiceProblem.unreachable ||
  MultiplayerServiceProblem.unavailable ||
  MultiplayerServiceProblem.turnedOff ||
  MultiplayerServiceProblem.refused ||
  MultiplayerServiceProblem.invalidAnswer => JoinFailureCode.serviceUnavailable,
}, detail: failure.message);

/// What [outcome] means for the player. No sentence repeats what the
/// service sent.
_Failure _failureOf(ControlPlaneOutcome outcome, _Subject subject) {
  final problem = outcome.problem!;
  final wait = outcome.retryAfter;
  final later = _tryAgain(wait);
  final message = switch (problem) {
    MultiplayerServiceProblem.offline =>
      'This computer is not connected to a network.',
    MultiplayerServiceProblem.unreachable when outcome.timedOut =>
      'The TopiaForge service did not answer in time. $later',
    MultiplayerServiceProblem.unreachable =>
      'The TopiaForge service could not be reached. $later',
    MultiplayerServiceProblem.unavailable =>
      'The TopiaForge service is unavailable right now. $later',
    MultiplayerServiceProblem.turnedOff =>
      'The TopiaForge service has turned this off for now. Try again later.',
    MultiplayerServiceProblem.rateLimited when outcome.heldBack =>
      'This computer asked the TopiaForge service too often just now. $later',
    MultiplayerServiceProblem.rateLimited =>
      'The TopiaForge service is turning away requests from this network '
          'for a moment. $later',
    MultiplayerServiceProblem.notFound when subject == _Subject.invite =>
      _inviteMiss.message,
    MultiplayerServiceProblem.notFound => 'That server is not listed any more.',
    MultiplayerServiceProblem.refused =>
      'The TopiaForge service refused a request from this launcher; the '
          'launcher may need an update.',
    MultiplayerServiceProblem.invalidAnswer => _unreadable.message,
    MultiplayerServiceProblem.notConfigured ||
    MultiplayerServiceProblem.descriptorInvalid =>
      'The TopiaForge service cannot be used right now. $later',
  };
  return _Failure(problem, message, retryAfter: wait);
}

String _tryAgain(Duration? wait) {
  if (wait == null) return 'Try again shortly.';
  final seconds = (wait.inMilliseconds + 999) ~/ 1000;
  if (seconds < 120) {
    return 'Try again in $seconds ${seconds == 1 ? 'second' : 'seconds'}.';
  }
  return 'Try again in ${(seconds + 59) ~/ 60} minutes.';
}
