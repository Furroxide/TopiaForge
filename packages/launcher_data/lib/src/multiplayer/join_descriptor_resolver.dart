/// Answers the domain's descriptor requests under its contact rules.
library;

import 'dart:io';

import 'package:launcher_domain/launcher_domain.dart';

import '../cancellation_token.dart';
import 'control_plane.dart';
import 'descriptor_client.dart';
import 'lan_discovery_client.dart';
import 'multiplayer_store.dart';

/// Looks a host name up; the seam tests use to watch what is resolved.
typedef HostLookup = Future<List<InternetAddress>> Function(String host);

Future<List<InternetAddress>> _systemLookup(String host) =>
    InternetAddress.lookup(host);

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
///   rules allow are contacted, IPv4 first. A local name that resolves only
///   beyond the local network is refused before any connection.
/// - A typed address names no server, so the first server it serves is
///   remembered, and a different one later is an identity change unless the
///   join already expects that server.
///
/// No event or detail names a host, a name or an address.
final class JoinDescriptorResolver {
  JoinDescriptorResolver({
    required DescriptorClient descriptors,
    required MultiplayerStore store,
    ControlPlaneClient controlPlane = const UnavailableControlPlane(),
    LanDiscoveryClient? lan,
    HostLookup? lookup,
    DateTime Function()? clock,
  }) : _descriptors = descriptors,
       _store = store,
       _controlPlane = controlPlane,
       _lan = lan,
       _lookup = lookup ?? _systemLookup,
       _clock = clock ?? DateTime.now;

  final DescriptorClient _descriptors;
  final MultiplayerStore _store;
  final ControlPlaneClient _controlPlane;
  final LanDiscoveryClient? _lan;
  final HostLookup _lookup;
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
    final List<InternetAddress> candidates;
    final literal = InternetAddress.tryParse(host);
    if (literal != null) {
      candidates = _contactable(request, [literal]);
      if (candidates.isEmpty) return _directNotAllowed;
    } else {
      // Looking a name up asks DNS servers its author chose, which can learn
      // the player's resolver or subnet; relay-only looks up local names only.
      if (!request.allowsDirect && !_isLocalName(host)) {
        return const DescriptorFetchFailed(
          DescriptorFetchFailure.directNotAllowed,
          'Looking up this name would reach servers its author chose. Allow '
          'a direct path for this session to use it.',
        );
      }
      final List<InternetAddress> resolved;
      try {
        resolved = await _lookup(host);
      } on Object {
        return const DescriptorFetchFailed(
          DescriptorFetchFailure.unreachable,
          'The host name could not be found.',
        );
      }
      if (cancellation?.isCancelled ?? false) return _cancelled;
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
    DescriptorFetchFailed? reported;
    for (final address in candidates) {
      final fetch = await _descriptors.fetchFrom(
        address,
        port,
        allowDirect: request.allowsDirect,
        expectedServerId: request.expectedServerId,
        cancellation: cancellation,
      );
      if (fetch is! DescriptorFetchFailed) return fetch;
      if (fetch.failure == DescriptorFetchFailure.cancelled) return fetch;
      if (reported == null || _rank(fetch) > _rank(reported)) reported = fetch;
    }
    return reported!;
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

  /// When several addresses fail, the most telling failure is reported:
  /// evidence about the server outweighs a busy host, which outweighs one
  /// that never answered.
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
    DescriptorFetchFailure.offline || DescriptorFetchFailure.cancelled => 0,
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
          DescriptorFetchFailure.offline => JoinFailed(
            JoinFailureCode.offline,
            detail: message,
          ),
          DescriptorFetchFailure.rateLimited => JoinFailed(
            JoinFailureCode.rateLimited,
            detail: message,
          ),
          DescriptorFetchFailure.unreachable ||
          DescriptorFetchFailure.timedOut ||
          DescriptorFetchFailure.busy ||
          DescriptorFetchFailure.notServing => JoinFailed(
            JoinFailureCode.unreachable,
            detail: message,
          ),
          DescriptorFetchFailure.refused ||
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
  Future<JoinEvent> _accepted(
    VerifiedServerDescriptor descriptor,
    JoinDescriptorRequest request,
  ) async {
    final target = request.target;
    if (target is! JoinAddressTarget) return JoinDescriptorVerified(descriptor);
    final served = descriptor.serverId;
    var changed = false;
    try {
      await _store.updateKnownHosts((hosts) {
        if (request.expectedServerId == null &&
            hosts.check(target, served) == KnownHostCheck.mismatch) {
          changed = true;
          return hosts;
        }
        return hosts.remember(target, served, _clock());
      });
    } on MultiplayerStoreException {
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
}
