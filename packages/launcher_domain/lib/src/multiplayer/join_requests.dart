part of '../multiplayer.dart';

/// Whom the data layer may contact on a join's behalf. A join exposes these
/// rules with each step that reaches out: [JoinSessionState.descriptorRequest]
/// while fetching the descriptor and [JoinSessionState.downloadRequest]
/// while downloading.
///
/// The data layer must keep to them:
///
/// - **Relay-only contacts no peer beyond the local network**, not for the
///   descriptor and not for any package:
///   - an IP literal is contacted only when [NetworkHost.pathKindOf] calls it
///     [NetworkPathKind.lan]. [JoinSession.start] refuses any other;
///   - a DNS name is looked up only when [NetworkHost.isLocalName] allows it
///     (`localhost`, or a name ending in `.localhost` or `.local`), since
///     looking up any other name reaches DNS servers its author chose.
///     [JoinSession.start] refuses any other before a lookup. Of what a local
///     name resolves to, only [contactableAddresses] are contacted; if none
///     is left, report [JoinFailureCode.directNotAllowed];
///   - a LAN listing's host is contacted only when
///     [NetworkHost.pathKindOfAddress] puts the beacon's sender on the local
///     network; otherwise report [JoinFailureCode.directNotAllowed];
///   - for invite, server-link and Internet-listing targets the descriptor
///     comes from the control plane, never from the host, and nothing is
///     downloaded from the host: its location is unknown
///     ([JoinSessionState.mayContactHost]). The service is not a peer.
/// - Under [MultiplayerAddressPrivacy.direct], any resolved address may be
///   contacted, IPv4 first.
/// - Nothing logged or reported names [JoinTarget.directHost] or a resolved
///   address.
sealed class JoinContactRules {
  const JoinContactRules._({required this.target, required this.privacy});

  final JoinTarget target;

  /// The privacy the player chose for this session before it started.
  final MultiplayerAddressPrivacy privacy;

  bool get allowsDirect => privacy == MultiplayerAddressPrivacy.direct;

  /// The resolved addresses of a name that may be contacted, IPv4 (4-byte)
  /// before IPv6 (16-byte) as the game orders them: every address when
  /// [allowsDirect], otherwise only those on the local network. An empty
  /// result for a non-empty [resolved] under relay-only is
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
}

/// What to fetch while a join is in [JoinStage.fetchingDescriptor], under
/// the [JoinContactRules].
///
/// - Check what is fetched with [ServerDescriptorVerifier], against
///   [expectedServerId] when it is set.
/// - A [JoinAddressTarget] names no server. While [expectedServerId] is
///   null, check [KnownHosts] for its host, port and the server it served:
///   on a mismatch report `JoinFailed.identityChanged` with the served server
///   id; otherwise remember the server once its descriptor verifies.
/// - Once [expectedServerId] is set for a typed address, because the join
///   learned it earlier or the player chose to trust it after an identity
///   change, accept only that server. A descriptor for another one
///   ([ServerDescriptorRefused.servedServerId]) is `JoinFailed.identityChanged`
///   as well. Remember the accepted server, replacing any older record.
final class JoinDescriptorRequest extends JoinContactRules {
  const JoinDescriptorRequest._({
    required super.target,
    required super.privacy,
    required this.expectedServerId,
  }) : super._();

  /// The server the descriptor must belong to; null only for a typed address
  /// before it has served one.
  final String? expectedServerId;

  @override
  String toString() =>
      'descriptor request for a $target, ${privacy.wireName}'
      '${expectedServerId == null ? '' : ', pinned'}';
}

/// What to download while a join is in [JoinStage.downloading], under the
/// [JoinContactRules].
///
/// - [registryDownloads] come from the player's own enabled sources, which
///   are not peers. Each archive must match its pinned SHA-256 and carry the
///   manifest the source advertised; report [JoinFailureCode.integrityFailed]
///   or [JoinFailureCode.manifestMismatch] otherwise.
/// - [hostDownloads] (developer mode only) come from the host's own
///   descriptor endpoint, with `TFD1 package <sha256>`, under exactly the
///   rules the descriptor fetch had. Each archive must match its SHA-256 and
///   hold a manifest with the claimed id and version.
/// - When those rules now forbid reaching the host, because a local name
///   resolves to no local address or a LAN listing's sender is off the local
///   network, report [JoinFailureCode.directNotAllowed]; the join ends.
final class JoinDownloadRequest extends JoinContactRules {
  JoinDownloadRequest._({
    required super.target,
    required super.privacy,
    required this.serverId,
    required List<PackageInstallAction> registryDownloads,
    required List<DescriptorPackage> hostDownloads,
  }) : registryDownloads = List.unmodifiable(registryDownloads),
       hostDownloads = List.unmodifiable(hostDownloads),
       super._();

  /// The server the packages are for.
  final String serverId;

  final List<PackageInstallAction> registryDownloads;
  final List<DescriptorPackage> hostDownloads;

  @override
  String toString() =>
      'download request for a $target, ${privacy.wireName}: '
      '${registryDownloads.length} from sources, ${hostDownloads.length} from '
      'the host';
}
