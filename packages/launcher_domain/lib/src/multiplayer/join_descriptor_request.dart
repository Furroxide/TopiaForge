part of '../multiplayer.dart';

/// What the data layer must fetch while a join is in
/// [JoinStage.fetchingDescriptor], and whom it may contact to do so. A join
/// in that stage exposes it as [JoinSessionState.descriptorRequest].
///
/// The data layer must keep to this contract:
///
/// - **Relay-only contacts no peer beyond the local network, not even for
///   the descriptor.** Under [MultiplayerAddressPrivacy.relayOnly]:
///   - an IP literal is contacted only when [NetworkHost.pathKindOf] calls it
///     [NetworkPathKind.lan], and [JoinSession.start] already refuses one
///     that is not;
///   - a DNS name is resolved first, and only [contactableAddresses] are
///     contacted. When the name resolves but none of its addresses is on the
///     local network, report [JoinFailureCode.directNotAllowed];
///   - a LAN listing's descriptor comes from the beacon's sender only when
///     [NetworkHost.pathKindOfAddress] puts that sender on the local
///     network; otherwise report [JoinFailureCode.directNotAllowed];
///   - invite, server-link and Internet-listing descriptors come from the
///     control plane, never from the host. The service is not a peer.
/// - Under [MultiplayerAddressPrivacy.direct], any resolved address may be
///   contacted, IPv4 first.
/// - Whatever is fetched is checked with [ServerDescriptorVerifier], against
///   [expectedServerId] when it is set.
/// - A [JoinAddressTarget] names no server. Check [KnownHosts] for its host,
///   port and the server id it served: on a mismatch report
///   [JoinFailureCode.identityChanged] unless [trustChangedIdentity];
///   otherwise remember that server id once its descriptor verifies.
/// - Nothing logged or reported names [JoinTarget.directHost] or a resolved
///   address.
final class JoinDescriptorRequest {
  const JoinDescriptorRequest._({
    required this.target,
    required this.expectedServerId,
    required this.privacy,
    required this.trustChangedIdentity,
  });

  final JoinTarget target;

  /// The server the descriptor must belong to; null only for a typed
  /// address before its first descriptor.
  final String? expectedServerId;

  /// The privacy the player chose for this session before it started.
  final MultiplayerAddressPrivacy privacy;

  /// Whether the player chose to trust a typed address whose server changed.
  final bool trustChangedIdentity;

  bool get allowsDirect => privacy == MultiplayerAddressPrivacy.direct;

  /// The resolved addresses of a DNS name that may be contacted, IPv4
  /// (4-byte) before IPv6 (16-byte) as the game orders them: every address
  /// when [allowsDirect], otherwise only those on the local network. An
  /// empty result for a non-empty [resolved] under relay-only is
  /// [JoinFailureCode.directNotAllowed].
  List<Uint8List> contactableAddresses(Iterable<List<int>> resolved) {
    bool shaped(List<int> address, int length) =>
        address.length == length &&
        address.every((value) => value >= 0 && value <= 0xFF);
    final ordered = [
      for (final address in resolved)
        if (shaped(address, 4)) Uint8List.fromList(address),
      for (final address in resolved)
        if (shaped(address, 16)) Uint8List.fromList(address),
    ];
    if (allowsDirect) return ordered;
    return [
      for (final address in ordered)
        if (NetworkHost.pathKindOfAddress(address) == NetworkPathKind.lan)
          address,
    ];
  }

  @override
  String toString() =>
      'descriptor request for a $target, ${privacy.wireName}'
      '${trustChangedIdentity ? ', trusting a changed identity' : ''}';
}
