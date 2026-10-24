part of '../launch_resolution.dart';

/// Where a [SessionHandoff] asks the launcher to join. None holds an address:
/// a server typed by address in game goes to the launcher as a
/// `topiaforge://join/…` link instead, and [LaunchHandoffCurrent] reuses the
/// target of the process's own launch request, which the launcher keeps.
sealed class LaunchHandoffTarget {
  const LaunchHandoffTarget._();

  factory LaunchHandoffTarget.fromJson(Object? value) {
    if (value is! Map<String, Object?>) {
      throw const FormatException('Invalid handoff target.');
    }
    switch (value['kind']) {
      case 'invite':
        final json = _object(
          value,
          {'inviteCode', 'kind'},
          {'inviteCode', 'kind'},
        );
        return LaunchHandoffInvite(_inviteCode(json['inviteCode']));
      case 'listing':
        const members = {'kind', 'serverId', 'source'};
        final json = _object(value, members, members);
        return LaunchHandoffListing(
          serverId: _sha256Hex(json['serverId']),
          source: _named(
            json['source'],
            ServerListingSource.values,
            (item) => item.name,
          ),
        );
      case 'current':
        _object(value, {'kind'}, {'kind'});
        return const LaunchHandoffCurrent();
      default:
        throw const FormatException('Unknown handoff target kind.');
    }
  }

  String get kind;

  Map<String, Object?> toJson();
}

final class LaunchHandoffInvite extends LaunchHandoffTarget {
  LaunchHandoffInvite(String inviteCode)
    : inviteCode = _inviteCode(inviteCode),
      super._();

  final String inviteCode;

  @override
  String get kind => 'invite';

  @override
  Map<String, Object?> toJson() => {'inviteCode': inviteCode, 'kind': kind};
}

final class LaunchHandoffListing extends LaunchHandoffTarget {
  LaunchHandoffListing({required String serverId, required this.source})
    : serverId = _sha256Hex(serverId),
      super._();

  final String serverId;
  final ServerListingSource source;

  @override
  String get kind => 'listing';

  @override
  Map<String, Object?> toJson() => {
    'kind': kind,
    'serverId': serverId,
    'source': source.name,
  };
}

/// The session this process was launched to join, planned once more: its
/// admission changed, or its content no longer matches.
final class LaunchHandoffCurrent extends LaunchHandoffTarget {
  const LaunchHandoffCurrent() : super._();

  @override
  String get kind => 'current';

  @override
  Map<String, Object?> toJson() => {'kind': kind};
}

/// The game asks the launcher that owns its process to run the join flow
/// (`session-handoff-<request key>.json`, game to launcher): the launcher
/// plans, asks consent, installs, and restarts into the server profile
/// through its owned-restart path. [requestId] is the launch request the
/// process was started with; [handoffId] names this handoff.
final class SessionHandoff {
  SessionHandoff({
    required String requestId,
    required String handoffId,
    required this.target,
  }) : requestId = _token(requestId),
       handoffId = _token(handoffId);

  static const schemaVersion = 1;

  factory SessionHandoff.fromJson(Object? value) {
    const members = {'handoffId', 'requestId', 'schemaVersion', 'target'};
    final json = _object(value, members, members);
    if (_integer(json['schemaVersion']) != schemaVersion) {
      throw const FormatException('Unsupported session handoff version.');
    }
    return SessionHandoff(
      requestId: _token(json['requestId']),
      handoffId: _token(json['handoffId']),
      target: LaunchHandoffTarget.fromJson(json['target']),
    );
  }

  final String requestId;
  final String handoffId;
  final LaunchHandoffTarget target;

  Map<String, Object?> toJson() =>
      _sorted({
            'handoffId': handoffId,
            'requestId': requestId,
            'schemaVersion': schemaVersion,
            'target': target.toJson(),
          })!
          as Map<String, Object?>;
}

/// The launcher asks a running game to join a server live, because the
/// game's packages already match the server's (`session-join-<request
/// key>.json`, launcher to game).
///
/// - [requestId] is the running process's launch request, and
///   [packageSetDigest] the package-set digest it runs: the game refuses a
///   join whose digest is not its own.
/// - The join reports progress and outcomes V2 under its own [joinId], as a
///   launch request would.
/// - [session] is a join. Its target is an invite or a listing, never an
///   address: a direct target needs a launch request, the only place an
///   address may be staged.
final class SessionJoinRequest {
  SessionJoinRequest({
    required String requestId,
    required String joinId,
    required String packageSetDigest,
    required this.session,
  }) : requestId = _token(requestId),
       joinId = _token(joinId),
       packageSetDigest = _digest(packageSetDigest) {
    if (session.role != LaunchSessionRole.join ||
        session.target is LaunchDirectTarget ||
        this.joinId == this.requestId) {
      throw const FormatException(
        'A live join joins an invite or a listing under its own id.',
      );
    }
  }

  static const schemaVersion = 1;

  factory SessionJoinRequest.fromJson(Object? value) {
    const members = {
      'joinId',
      'packageSetDigest',
      'requestId',
      'schemaVersion',
      'session',
    };
    final json = _object(value, members, members);
    if (_integer(json['schemaVersion']) != schemaVersion) {
      throw const FormatException('Unsupported session join version.');
    }
    return SessionJoinRequest(
      requestId: _token(json['requestId']),
      joinId: _token(json['joinId']),
      packageSetDigest: _digest(json['packageSetDigest']),
      session: LaunchSessionRequest.fromJson(json['session']),
    );
  }

  final String requestId;
  final String joinId;
  final String packageSetDigest;
  final LaunchSessionRequest session;

  Map<String, Object?> toJson() =>
      _sorted({
            'joinId': joinId,
            'packageSetDigest': packageSetDigest,
            'requestId': requestId,
            'schemaVersion': schemaVersion,
            'session': session.toJson(),
          })!
          as Map<String, Object?>;
}

/// The launcher asks a game it started to quit, as the game's own Quit button
/// would, before it restarts the game (`launcher-quit-<request key>.json`,
/// launcher to game). Only the process launched as [requestId] acts on it, so
/// a launcher can never ask a game it did not start to quit. [quitId] names
/// this request, so the launcher withdraws exactly the one it wrote.
final class LauncherQuitRequest {
  LauncherQuitRequest({required String requestId, required String quitId})
    : requestId = _token(requestId),
      quitId = _token(quitId);

  static const schemaVersion = 1;

  factory LauncherQuitRequest.fromJson(Object? value) {
    const members = {'quitId', 'requestId', 'schemaVersion'};
    final json = _object(value, members, members);
    if (_integer(json['schemaVersion']) != schemaVersion) {
      throw const FormatException('Unsupported launcher quit version.');
    }
    return LauncherQuitRequest(
      requestId: _token(json['requestId']),
      quitId: _token(json['quitId']),
    );
  }

  final String requestId;
  final String quitId;

  Map<String, Object?> toJson() =>
      _sorted({
            'quitId': quitId,
            'requestId': requestId,
            'schemaVersion': schemaVersion,
          })!
          as Map<String, Object?>;
}
