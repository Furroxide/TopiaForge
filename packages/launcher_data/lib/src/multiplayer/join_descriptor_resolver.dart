/// Answers the domain's descriptor requests under its contact rules.
library;

import 'dart:async';
import 'dart:io';

import 'package:launcher_domain/launcher_domain.dart';

import '../cancellation_token.dart';
import 'control_plane.dart';
import 'descriptor_client.dart';
import 'lan_discovery_client.dart';
import 'multiplayer_store.dart';

/// Looks a host name up; the seam tests use to watch what is resolved.
typedef HostLookup = Future<List<InternetAddress>> Function(String host);

/// Whether this computer has any network up apart from loopback; the seam
/// tests use in place of the system's interfaces.
typedef NetworkPresence = Future<bool> Function();

Future<List<InternetAddress>> _systemLookup(String host) =>
    InternetAddress.lookup(host);

Future<bool> _anyNetworkUp() async {
  final networks = await NetworkInterface.list();
  return networks.any((network) => network.addresses.isNotEmpty);
}

/// Fetches the descriptor a [JoinDescriptorRequest] asks for and turns the
/// outcome into the [JoinEvent] the join reducer expects, keeping to the
/// request's [JoinContactRules]:
///
/// - Invites, server links and Internet listings get their descriptor from
///   the multiplayer service and never from the host.
/// - A LAN listing is fetched from where its beacon came; under relay-only a
///   sender beyond the local network is refused before anything is
///   contacted.
/// - A direct link or typed address that is an IP literal is contacted only
///   when the rules allow it. A name is looked up only when relay-only allows
///   it (a local name), and of what it resolves to only the addresses the
///   rules allow are contacted, IPv4 first. The lookup and those addresses
///   share [fetchBudget], and cancelling stops waiting for the lookup at
///   once. A local name that resolves only beyond the local network is
///   refused before any connection.
/// - A typed address names no server, so the first server it serves is
///   remembered, and a different one later is an identity change unless the
///   join already expects that server. When the record cannot be read, no
///   server is taken on trust: the player is asked as for a change.
///
/// No event or detail names a host, a name or an address.
final class JoinDescriptorResolver {
  JoinDescriptorResolver({
    required DescriptorClient descriptors,
    required MultiplayerStore store,
    ControlPlaneClient controlPlane = const UnavailableControlPlane(),
    LanDiscoveryClient? lan,
    HostLookup? lookup,
    NetworkPresence? networkPresence,
    DescriptorTimer? timer,
    DateTime Function()? clock,
  }) : _descriptors = descriptors,
       _store = store,
       _controlPlane = controlPlane,
       _lan = lan,
       _lookup = lookup ?? _systemLookup,
       _networkPresence = networkPresence ?? _anyNetworkUp,
       _timer = timer ?? Timer.new,
       _clock = clock ?? DateTime.now;

  /// The longest one target takes, however many addresses its name has: its
  /// lookup and then the addresses, within one address's connect and response
  /// deadlines and a connect deadline more for the next.
  static const fetchBudget = Duration(seconds: 15);

  final DescriptorClient _descriptors;
  final MultiplayerStore _store;
  final ControlPlaneClient _controlPlane;
  final LanDiscoveryClient? _lan;
  final HostLookup _lookup;
  final NetworkPresence _networkPresence;
  final DescriptorTimer _timer;
  final DateTime Function() _clock;

  /// The event that answers [request]; null only when [cancellation] stopped
  /// it, since the reducer already heard the cancel itself. Never throws.
  Future<JoinEvent?> answer(
    JoinDescriptorRequest request, {
    CancellationToken? cancellation,
  }) async {
    if (cancellation?.isCancelled ?? false) return null;
    final target = request.target;
    if (target.resolvesThroughService) {
      return _controlPlane.fetchDescriptor(request, cancellation: cancellation);
    }
    final DescriptorFetch fetch;
    switch (target) {
      case JoinListingTarget():
        final lan = _lan;
        fetch = lan == null
            ? const DescriptorFetchFailed(
                DescriptorFetchFailure.unreachable,
                'The session is no longer on the local network.',
              )
            : await lan.fetchDescriptor(request, cancellation: cancellation);
      case JoinLinkTarget() || JoinAddressTarget():
        fetch = await _fetchDirect(
          request,
          target.directHost!,
          target.directPort!,
          cancellation,
        );
    }
    return _eventOf(fetch, request);
  }

  Future<DescriptorFetch> _fetchDirect(
    JoinDescriptorRequest request,
    String host,
    int port,
    CancellationToken? cancellation,
  ) async {
    final literal = InternetAddress.tryParse(host);
    final allowed = literal == null ? null : _contactable(request, [literal]);
    if (allowed != null && allowed.isEmpty) return _directNotAllowed;
    // Looking a name up asks DNS servers its author chose, which can learn
    // the player's resolver or subnet; relay-only looks up local names only.
    if (literal == null && !request.allowsDirect && !_isLocalName(host)) {
      return const DescriptorFetchFailed(
        DescriptorFetchFailure.directNotAllowed,
        'Looking up this name would reach servers its author chose. Allow '
        'a direct path for this session to use it.',
      );
    }
    final budget = _FetchBudget(_timer, fetchBudget, cancellation);
    try {
      final List<InternetAddress> candidates;
      if (allowed != null) {
        candidates = allowed;
      } else {
        final List<InternetAddress>? resolved;
        try {
          resolved = await _lookupWithin(host, budget.token);
        } on Object {
          return const DescriptorFetchFailed(
            DescriptorFetchFailure.unreachable,
            'The host name could not be found.',
          );
        }
        if (cancellation?.isCancelled ?? false) return _cancelled;
        if (resolved == null) return _lookupOutOfTime;
        candidates = _contactable(request, resolved);
        if (candidates.isEmpty) {
          return resolved.isEmpty
              ? const DescriptorFetchFailed(
                  DescriptorFetchFailure.unreachable,
                  'The host name has no usable address.',
                )
              : _directNotAllowed;
        }
      }
      return await _fetchFirst(request, candidates, port, budget, cancellation);
    } finally {
      budget.dispose();
    }
  }

  /// Looks [host] up until [budget] is cancelled; null when it is first. A
  /// lookup cannot be stopped, so one still running then finishes unheard.
  Future<List<InternetAddress>?> _lookupWithin(
    String host,
    CancellationToken budget,
  ) {
    if (budget.isCancelled) return Future.value();
    final outcome = Completer<List<InternetAddress>?>();
    final registration = budget.register(() {
      if (!outcome.isCompleted) outcome.complete();
    });
    Future.sync(() => _lookup(host)).then(
      (resolved) {
        if (!outcome.isCompleted) outcome.complete(resolved);
      },
      onError: (Object error, StackTrace stack) {
        if (!outcome.isCompleted) outcome.completeError(error, stack);
      },
    );
    return outcome.future.whenComplete(registration.dispose);
  }

  /// Tries [candidates] in order with what remains of [budget], stopping at
  /// the first descriptor. The budget cancels a fetch still running when it
  /// ends.
  Future<DescriptorFetch> _fetchFirst(
    JoinDescriptorRequest request,
    List<InternetAddress> candidates,
    int port,
    _FetchBudget budget,
    CancellationToken? cancellation,
  ) async {
    DescriptorFetchFailed? reported;
    for (final address in candidates) {
      if (budget.token.isCancelled) break;
      final fetch = await _descriptors.fetchFrom(
        address,
        port,
        allowDirect: request.allowsDirect,
        expectedServerId: request.expectedServerId,
        cancellation: budget.token,
      );
      if (fetch is! DescriptorFetchFailed) return fetch;
      if (fetch.failure == DescriptorFetchFailure.cancelled) break;
      if (reported == null || _rank(fetch) > _rank(reported)) {
        reported = fetch;
      }
    }
    if (cancellation?.isCancelled ?? false) return _cancelled;
    if (budget.expired &&
        (reported == null || _rank(_outOfTime) > _rank(reported))) {
      reported = _outOfTime;
    }
    return reported ?? _outOfTime;
  }

  /// The addresses of [resolved] the rules allow, in the order they allow
  /// them, as the lookup returned them (so a scoped address keeps its scope).
  static List<InternetAddress> _contactable(
    JoinContactRules rules,
    List<InternetAddress> resolved,
  ) {
    final remaining = [
      for (final address in resolved)
        if (address.type == InternetAddressType.IPv4 ||
            address.type == InternetAddressType.IPv6)
          address,
    ];
    final allowed = rules.contactableAddresses([
      for (final address in remaining) address.rawAddress,
    ]);
    final result = <InternetAddress>[];
    for (final raw in allowed) {
      final index = remaining.indexWhere(
        (address) => _sameBytes(address.rawAddress, raw),
      );
      if (index >= 0) result.add(remaining.removeAt(index));
    }
    return result;
  }

  static bool _isLocalName(String host) {
    try {
      return NetworkHost.isLocalName(host);
    } on ArgumentError {
      return false;
    }
  }

  /// Whether any network is up; true when that cannot be told, so offline
  /// is reported only when it is certain.
  Future<bool> _anyNetwork() async {
    try {
      return await _networkPresence();
    } on Object {
      return true;
    }
  }

  /// When several addresses fail, the most telling failure is reported:
  /// evidence about the server outweighs a busy host, which outweighs one
  /// that never answered. No route ranks last, so it is reported only when
  /// every address had none.
  static int _rank(DescriptorFetchFailed failed) => switch (failed.failure) {
    DescriptorFetchFailure.directNotAllowed => 7,
    DescriptorFetchFailure.otherServer => 6,
    DescriptorFetchFailure.badSignature ||
    DescriptorFetchFailure.invalidDescriptor ||
    DescriptorFetchFailure.malformedResponse => 5,
    DescriptorFetchFailure.rateLimited => 4,
    DescriptorFetchFailure.busy ||
    DescriptorFetchFailure.notServing ||
    DescriptorFetchFailure.refused => 3,
    DescriptorFetchFailure.timedOut => 2,
    DescriptorFetchFailure.unreachable => 1,
    DescriptorFetchFailure.noRoute || DescriptorFetchFailure.cancelled => 0,
  };

  Future<JoinEvent?> _eventOf(
    DescriptorFetch fetch,
    JoinDescriptorRequest request,
  ) async {
    switch (fetch) {
      case DescriptorFetched(:final descriptor):
        return _accepted(descriptor, request);
      case DescriptorFetchFailed(
        :final failure,
        :final message,
        :final servedServerId,
      ):
        return switch (failure) {
          DescriptorFetchFailure.cancelled => null,
          DescriptorFetchFailure.directNotAllowed => JoinFailed(
            JoinFailureCode.directNotAllowed,
            detail: message,
          ),
          // No route to one destination, such as an IPv6 address on a
          // network with IPv4 only, is not being offline.
          DescriptorFetchFailure.noRoute =>
            await _anyNetwork()
                ? JoinFailed(JoinFailureCode.unreachable, detail: message)
                : JoinFailed(
                    JoinFailureCode.offline,
                    detail: 'This computer is not connected to a network.',
                  ),
          DescriptorFetchFailure.rateLimited => JoinFailed(
            JoinFailureCode.rateLimited,
            detail: message,
          ),
          // The game reports every status but 200 as unreachable, `TFD1 400`
          // and unknown ones included.
          DescriptorFetchFailure.unreachable ||
          DescriptorFetchFailure.timedOut ||
          DescriptorFetchFailure.busy ||
          DescriptorFetchFailure.notServing ||
          DescriptorFetchFailure.refused => JoinFailed(
            JoinFailureCode.unreachable,
            detail: message,
          ),
          DescriptorFetchFailure.malformedResponse ||
          DescriptorFetchFailure.invalidDescriptor ||
          DescriptorFetchFailure.badSignature => JoinFailed(
            JoinFailureCode.descriptorInvalid,
            detail: message,
          ),
          DescriptorFetchFailure.otherServer =>
            request.target is JoinAddressTarget &&
                    WireText.isHex(servedServerId, 32)
                ? JoinFailed.identityChanged(
                    servedServerId: servedServerId!,
                    detail: 'This address now serves a different server.',
                  )
                : JoinFailed(
                    JoinFailureCode.descriptorInvalid,
                    detail: 'The descriptor belongs to another server.',
                  ),
        };
    }
  }

  /// Trust on first use for a typed address: a server it has not served
  /// before is an identity change while the join expects no server; once it
  /// does, the client already refused any other. The accepted server is
  /// remembered either way, replacing an older record.
  ///
  /// A record that cannot be read cannot vouch for anyone, so a join that
  /// expects no server fails closed: the player is asked to trust the server
  /// as for a change, and the record is left as it is.
  Future<JoinEvent> _accepted(
    VerifiedServerDescriptor descriptor,
    JoinDescriptorRequest request,
  ) async {
    final target = request.target;
    if (target is! JoinAddressTarget) return JoinDescriptorVerified(descriptor);
    final served = descriptor.serverId;
    final expectsServer = request.expectedServerId != null;
    var changed = false;
    try {
      await _store.updateKnownHosts((hosts) {
        if (!expectsServer &&
            hosts.check(target, served) == KnownHostCheck.mismatch) {
          changed = true;
          return hosts;
        }
        return hosts.remember(target, served, _clock());
      });
    } on MultiplayerStoreException catch (error) {
      if (error.unreadable && !expectsServer) {
        return JoinFailed.identityChanged(
          servedServerId: served,
          detail:
              'The record of the servers this address served before could '
              'not be read just now, so this server could not be checked. '
              'Make sure it is the server you expect before you trust it, or '
              'try again in a moment.',
        );
      }
      // An unsaved record only costs a prompt next time.
    }
    if (changed) {
      return JoinFailed.identityChanged(
        servedServerId: served,
        detail:
            'This address served a different server before. Make sure it is '
            'the server you expect before you trust it.',
      );
    }
    return JoinDescriptorVerified(descriptor);
  }

  static bool _sameBytes(List<int> left, List<int> right) {
    if (left.length != right.length) return false;
    for (var index = 0; index < left.length; index++) {
      if (left[index] != right[index]) return false;
    }
    return true;
  }

  static const _directNotAllowed = DescriptorFetchFailed(
    DescriptorFetchFailure.directNotAllowed,
    'Joining this host directly would show it your address. Allow a direct '
    'path for this session to continue.',
  );

  static const _cancelled = DescriptorFetchFailed(
    DescriptorFetchFailure.cancelled,
    'The request was cancelled.',
  );

  static const _outOfTime = DescriptorFetchFailed(
    DescriptorFetchFailure.timedOut,
    'The host did not answer in time.',
  );

  static const _lookupOutOfTime = DescriptorFetchFailed(
    DescriptorFetchFailure.timedOut,
    'The host name could not be looked up in time.',
  );
}

/// The time every step of one target shares: its name's lookup and each
/// address's fetch. [token] is cancelled when the time runs out, which sets
/// [expired], or when the join is cancelled.
final class _FetchBudget {
  _FetchBudget(
    DescriptorTimer timer,
    Duration length,
    CancellationToken? join,
  ) {
    _deadline = timer(length, () {
      expired = true;
      token.cancel();
    });
    _linked = join?.register(token.cancel);
  }

  final token = CancellationToken();
  var expired = false;
  late final Timer _deadline;
  CancellationRegistration? _linked;

  void dispose() {
    _deadline.cancel();
    _linked?.dispose();
  }
}
