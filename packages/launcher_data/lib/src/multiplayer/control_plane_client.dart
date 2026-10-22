/// The launcher's client for the TopiaForge multiplayer service
/// (`docs/internal/ControlPlaneApi.md`, sections 1, 2, 4, 5.1 and 6): its
/// status, the server browser, a server's detail and invite resolution, over
/// HTTPS to the origin this build names.
library;

import 'dart:async';
import 'dart:convert';

import 'package:crypto/crypto.dart';
import 'package:launcher_domain/launcher_domain.dart';

import 'control_plane.dart';
import 'control_plane_answers.dart';
import 'control_plane_pacing.dart';
import 'control_plane_requests.dart';
import 'descriptor_signature_verifier.dart';
import 'join_descriptor_resolver.dart' show NetworkPresence;

export 'control_plane_requests.dart'
    show ControlPlaneLog, ControlPlaneTimeouts, ControlPlaneTimer;

part 'control_plane_client_support.dart';

/// The client this build's [service] calls for: the HTTPS client when it
/// names a service, and otherwise [UnavailableControlPlane], which contacts
/// nothing.
ControlPlaneClient openControlPlane(
  MultiplayerServiceConfiguration service, {
  ControlPlaneLog? log,
}) {
  final origin = service.origin;
  return service.isConfigured && origin != null
      ? HttpsControlPlaneClient(origin, log: log)
      : UnavailableControlPlane(service.status);
}

/// The TopiaForge service at [origin], as the launcher uses it.
///
/// - **Trust.** A server answer's signed descriptor is used only once it
///   verifies against the server id ([ServerDescriptorVerifier]) and its
///   SHA-256, over the exact bytes served, is the hash the service named. A
///   join resolves first and verifies while fetching the descriptor, so a
///   descriptor that fails is [JoinFailureCode.descriptorInvalid].
/// - **Joins** read the status first, so a join learns at once that Internet
///   joining or invites are off. An invite is resolved with its code in the
///   body, never in a URL; a server link or Internet listing through the
///   server's detail. What resolving fetched answers the descriptor stage
///   that follows, once; a re-plan asks the service again.
/// - **Misses.** Every invite miss is the same answer, so a failure never
///   says which kind it was. A code that missed is not sent again for
///   [missMemory]; a text that spells no code is a miss without asking.
/// - **Privacy.** No log line, failure or message holds an invite code, an
///   address or text the service sent. Nothing is written to disk; misses
///   are remembered by the SHA-256 of their code.
///
/// Pacing, retries, deadlines and cancellation are [ControlPlaneRequester]'s.
final class HttpsControlPlaneClient implements ControlPlaneClient {
  HttpsControlPlaneClient(
    Uri origin, {
    ControlPlaneLog? log,
    ServerDescriptorVerifier verifier = const ServerDescriptorVerifier(
      Ed25519DescriptorSignatureVerifier(),
    ),
    DateTime Function()? clock,
    ControlPlaneTimer? timer,
    NetworkPresence? networkPresence,
    ControlPlaneTimeouts timeouts = const ControlPlaneTimeouts(),
  }) : _log = log,
       _verifier = verifier,
       _clock = clock ?? DateTime.now,
       _requests = ControlPlaneRequester(
         origin,
         log: log,
         pacer: ControlPlanePacer(clock: clock),
         timer: timer,
         networkPresence: networkPresence,
         timeouts: timeouts,
       );

  /// How long a status read stands in for the next join's.
  static const statusMaxAge = Duration(seconds: 30);

  /// How long what resolving fetched waits for the descriptor stage.
  static const resolvedMaxAge = Duration(minutes: 2);

  /// How long an invite code that missed is answered without asking again.
  static const missMemory = Duration(minutes: 10);

  final ControlPlaneLog? _log;
  final ServerDescriptorVerifier _verifier;
  final DateTime Function() _clock;
  final ControlPlaneRequester _requests;
  late final _recall = _Recall(_clock);
  (MultiplayerServiceFeatures, DateTime)? _status;

  @override
  MultiplayerServiceStatus get status => MultiplayerServiceStatus.configured;

  @override
  Future<MultiplayerServiceCheck> readStatus({
    CancellationToken? cancellation,
  }) async {
    final (features, failure) = await _features(cancellation, fresh: true);
    return features != null
        ? MultiplayerServiceCheck.available(features)
        : MultiplayerServiceCheck.unavailable(
            failure!.problem,
            '${failure.message} LAN and direct play still work.',
            retryAfter: failure.retryAfter,
          );
  }

  @override
  Future<InternetBrowseResult> browse(
    ServerBrowseFilter filter, {
    String? after,
    CancellationToken? cancellation,
  }) async {
    if (after != null && !WireText.isHex(after, 32)) {
      throw ArgumentError('A page follows a server id.', 'after');
    }
    final outcome = await _requests.send(
      ControlPlaneRequest(
        scope: ControlPlaneScope.browse,
        operation: 'server list',
        path: '/v1/servers',
        query: {
          'build': filter.gameBuild,
          'protocol': '${filter.protocolMajor}',
          'after': ?after,
          'limit': '${filter.pageSize}',
        },
        maxBytes: ControlPlaneBounds.page,
      ),
      cancellation: cancellation,
    );
    final page = outcome.succeeded
        ? readServerPage(outcome.bytes!, filter, after)
        : null;
    if (page != null) {
      return InternetBrowseResult.page(page.listings, next: page.next);
    }
    final failure = outcome.succeeded
        ? _unreadable
        : _failureOf(outcome, _Subject.servers);
    return InternetBrowseResult.unavailable(
      '${failure.message} LAN and direct play still work.',
      problem: failure.problem,
      retryAfter: failure.retryAfter,
    );
  }

  @override
  Future<InternetServerLookup> fetchServer(
    String serverId, {
    CancellationToken? cancellation,
  }) async {
    if (!WireText.isHex(serverId, 32)) {
      throw ArgumentError('Not a server id.', 'serverId');
    }
    final (answer, failure) = await _findServer(
      serverId,
      cancellation,
      joining: false,
    );
    return answer == null
        ? _lookupFailed(failure!)
        : _lookupFound(answer, cancellation);
  }

  /// Looks up the invite [code], as the player typed it or canonical, and
  /// verifies the server it leads to. The code travels in the request's body
  /// only, as typed, since the service reads it forgivingly. Throws
  /// [OperationCancelledException] when [cancellation] stops it, and nothing
  /// else.
  Future<InternetServerLookup> lookUpInvite(
    String code, {
    CancellationToken? cancellation,
  }) async {
    final (answer, failure) = await _findInvite(code, cancellation);
    return answer == null
        ? _lookupFailed(failure!)
        : _lookupFound(answer, cancellation);
  }

  @override
  Future<JoinEvent?> resolve(
    JoinTarget target, {
    CancellationToken? cancellation,
  }) async {
    if (!target.resolvesThroughService) {
      throw ArgumentError(
        'Only invites, server links and Internet listings are resolved.',
        'target',
      );
    }
    final code = _inviteOf(target);
    try {
      final (answer, failure) = code != null
          ? await _findInvite(code, cancellation)
          : await _findServer(target.pinnedServerId!, cancellation);
      cancellation?.throwIfCancelled();
      if (answer == null) return _joinFailed(failure!, invite: code != null);
      _recall.keep(target, answer);
      return JoinTargetResolved(answer.serverId);
    } on OperationCancelledException {
      return null;
    }
  }

  @override
  Future<JoinEvent?> fetchDescriptor(
    JoinDescriptorRequest request, {
    CancellationToken? cancellation,
  }) async {
    final target = request.target;
    if (!target.resolvesThroughService) {
      throw ArgumentError(
        'Only targets the service resolves get their descriptor from it.',
        'request',
      );
    }
    final expected = request.expectedServerId;
    if (expected == null) {
      return JoinFailed(
        JoinFailureCode.descriptorInvalid,
        detail: 'No server was named for this descriptor.',
      );
    }
    final code = _inviteOf(target);
    try {
      cancellation?.throwIfCancelled();
      var answer = _recall.take(target, expected);
      if (answer == null) {
        final (fetched, failure) = code != null
            ? await _findInvite(code, cancellation)
            : await _findServer(expected, cancellation);
        if (fetched == null) return _joinFailed(failure!, invite: code != null);
        if (fetched.serverId != expected) {
          return JoinFailed(
            JoinFailureCode.descriptorInvalid,
            detail:
                'The invite now leads to another server than the one '
                'checked before.',
          );
        }
        answer = fetched;
      }
      final (verified, refusal) = await _check(answer, expected);
      cancellation?.throwIfCancelled();
      return verified != null
          ? JoinDescriptorVerified(verified)
          : JoinFailed(JoinFailureCode.descriptorInvalid, detail: refusal!);
    } on OperationCancelledException {
      return null;
    }
  }

  /// Resolves the invite [text]: nothing is asked for a text that spells no
  /// code or a code that missed lately, and the status is read first.
  Future<(ServerAnswer?, _Failure?)> _findInvite(
    String text,
    CancellationToken? cancellation,
  ) async {
    final code = InviteCode.tryNormalize(text);
    if (code == null || _recall.missedLately(code)) {
      cancellation?.throwIfCancelled();
      _log?.call(
        'TopiaForge service: invite resolution missed without asking.',
      );
      return (null, _inviteMiss);
    }
    final gate = await _gate(cancellation, invite: true);
    if (gate != null) return (null, gate);
    final outcome = await _requests.send(
      ControlPlaneRequest(
        scope: ControlPlaneScope.invite,
        operation: 'invite resolution',
        method: 'POST',
        path: '/v1/invites/resolve',
        body: {'code': text},
        maxBytes: ControlPlaneBounds.server,
      ),
      cancellation: cancellation,
    );
    if (outcome.problem == MultiplayerServiceProblem.notFound) {
      _recall.missed(code);
    }
    return _serverOf(outcome, _Subject.invite);
  }

  /// Asks for the public server [serverId]; a join reads the status first.
  Future<(ServerAnswer?, _Failure?)> _findServer(
    String serverId,
    CancellationToken? cancellation, {
    bool joining = true,
  }) async {
    if (joining) {
      final gate = await _gate(cancellation, invite: false);
      if (gate != null) return (null, gate);
    }
    final outcome = await _requests.send(
      ControlPlaneRequest(
        scope: ControlPlaneScope.browse,
        operation: 'server detail',
        path: '/v1/servers/$serverId',
        maxBytes: ControlPlaneBounds.server,
      ),
      cancellation: cancellation,
    );
    return _serverOf(outcome, _Subject.server, serverId: serverId);
  }

  (ServerAnswer?, _Failure?) _serverOf(
    ControlPlaneOutcome outcome,
    _Subject subject, {
    String? serverId,
  }) {
    // An operator turned something off: the next join reads the status anew.
    if (outcome.problem == MultiplayerServiceProblem.turnedOff) _status = null;
    if (!outcome.succeeded) return (null, _failureOf(outcome, subject));
    final answer = readServerAnswer(outcome.bytes!, serverId: serverId);
    return answer == null ? (null, _unreadable) : (answer, null);
  }

  /// The status a join reads first: a refusal when the service cannot take
  /// the join now, or null.
  Future<_Failure?> _gate(
    CancellationToken? cancellation, {
    required bool invite,
  }) async {
    final (features, failure) = await _features(cancellation, fresh: false);
    if (features == null) return failure;
    if (!features.signaling) {
      return const _Failure(
        MultiplayerServiceProblem.turnedOff,
        'Joining over the Internet is turned off on the TopiaForge service '
        'for now. Try again later.',
      );
    }
    if (invite && !features.invites) {
      return const _Failure(
        MultiplayerServiceProblem.turnedOff,
        'Invites are turned off on the TopiaForge service for now. Try again '
        'later.',
      );
    }
    return null;
  }

  Future<(MultiplayerServiceFeatures?, _Failure?)> _features(
    CancellationToken? cancellation, {
    required bool fresh,
  }) async {
    final cached = _status;
    if (!fresh &&
        cached != null &&
        _clock().difference(cached.$2) < statusMaxAge) {
      return (cached.$1, null);
    }
    final outcome = await _requests.send(
      const ControlPlaneRequest(
        scope: ControlPlaneScope.status,
        operation: 'status',
        path: '/v1/status',
        maxBytes: ControlPlaneBounds.small,
      ),
      cancellation: cancellation,
    );
    if (!outcome.succeeded) return (null, _failureOf(outcome, _Subject.status));
    final features = readStatusAnswer(outcome.bytes!);
    if (features == null) return (null, _unreadable);
    _status = (features, _clock());
    return (features, null);
  }

  /// Verifies [answer]'s descriptor before anything in it is used: its
  /// SHA-256 over the exact bytes served must be the hash the service named,
  /// and it must verify for [serverId]. Returns why it did not otherwise.
  Future<(VerifiedServerDescriptor?, String?)> _check(
    ServerAnswer answer,
    String serverId,
  ) async {
    if (sha256.convert(answer.descriptorBytes).toString() !=
        answer.descriptorSha256) {
      return (
        null,
        'The server description from the TopiaForge service does not match '
            'its hash, so it was not used.',
      );
    }
    final verification = await _verifier.verify(
      answer.descriptorBytes,
      expectedServerId: serverId,
    );
    return switch (verification) {
      ServerDescriptorAccepted(:final descriptor) => (descriptor, null),
      ServerDescriptorRefused(:final message) => (null, message),
    };
  }

  Future<InternetServerLookup> _lookupFound(
    ServerAnswer answer,
    CancellationToken? cancellation,
  ) async {
    final (verified, refusal) = await _check(answer, answer.serverId);
    cancellation?.throwIfCancelled();
    if (verified == null) {
      return InternetServerLookup.failed(
        MultiplayerServiceProblem.descriptorInvalid,
        refusal!,
      );
    }
    final descriptor = verified.descriptor;
    if (answer.players > descriptor.maxPlayers) {
      return _lookupFailed(_unreadable);
    }
    return InternetServerLookup.found(
      InternetServer(
        descriptor: verified,
        listing: ServerListing(
          serverId: verified.serverId,
          name: descriptor.name,
          source: ServerListingSource.internet,
          players: answer.players,
          maxPlayers: descriptor.maxPlayers,
          passwordRequired: descriptor.passwordRequired,
          voice: descriptor.voice,
          gamemode: descriptor.gamemode,
          world: descriptor.world,
          gameBuild: descriptor.gameBuild,
          runtimeVersion: descriptor.runtimeVersion,
          descriptorSha256: verified.descriptorSha256,
        ),
        hostRelayOnly: answer.hostRelayOnly,
      ),
    );
  }
}
