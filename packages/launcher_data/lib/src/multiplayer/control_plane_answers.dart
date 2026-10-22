/// Strict readers for the TopiaForge service's answers
/// (`docs/internal/ControlPlaneApi.md`).
///
/// Each answer must be canonical JSON within its bound, as the service writes
/// every one, with every member this launcher uses present and valid: types,
/// ranges, the exact shapes of ids and hashes, and agreement with what was
/// asked. Members this launcher does not know are ignored, as the API allows.
/// A reader returns null for anything else. An error's message is never read,
/// and nothing here logs.
library;

import 'dart:convert';
import 'dart:typed_data';

import 'package:launcher_domain/launcher_domain.dart';

import 'control_plane_results.dart';

/// How large each kind of answer may be, in bytes.
abstract final class ControlPlaneBounds {
  /// The status, and every error.
  static const small = 4 * 1024;

  /// A page of at most 100 listings, each well under 2 KiB.
  static const page = 256 * 1024;

  /// A server answer carries a whole signed descriptor as a JSON string,
  /// which escaping can nearly double, beside its listing.
  static const server = 2 * SignedServerDescriptorV1.maximumBytes + 64 * 1024;
}

/// A page of the server list.
final class ServerListPage {
  const ServerListPage(this.listings, this.next);

  final List<ServerListing> listings;
  final String? next;
}

/// A server answer, from resolving an invite or asking for a public server:
/// read and consistent, but its descriptor is not verified yet. Nothing
/// from it but [serverId] may be used before [descriptorBytes] verify for
/// that id and hash to [descriptorSha256].
final class ServerAnswer {
  ServerAnswer._({
    required this.serverId,
    required this.descriptorBytes,
    required this.descriptorSha256,
    required this.players,
    required this.hostRelayOnly,
  });

  final String serverId;

  /// The signed descriptor's exact served bytes.
  final Uint8List descriptorBytes;

  /// The hash the service names for [descriptorBytes], and its listing too.
  final String descriptorSha256;

  /// The players the host's latest heartbeat reported.
  final int players;

  /// Whether the host keeps its address behind the relay.
  final bool hostRelayOnly;

  @override
  String toString() => 'server answer';
}

const _statusSchema = 'topiaforge.control-plane-status/1';
const _listingSchema = 'topiaforge.server-listing/1';

/// The latest Unix time the service may name: the end of the year 9999.
const _latestTime = 253402300799;

/// Every error the service's catalogue holds, with the one status it comes
/// with (ControlPlaneApi.md, section 1).
const _catalogue = {
  'invalid-request': 400,
  'invalid-descriptor': 400,
  'unsupported-visibility': 400,
  'challenge-invalid': 401,
  'proof-invalid': 401,
  'unauthorized': 401,
  'server-blocked': 403,
  'ticket-limit': 403,
  'not-found': 404,
  'method-not-allowed': 405,
  'stale-descriptor': 409,
  'invite-limit': 409,
  'room-full': 409,
  'payload-too-large': 413,
  'unsupported-media-type': 415,
  'rate-limited': 429,
  'internal': 500,
  'upstream-failed': 502,
  'feature-disabled': 503,
  'relay-unavailable': 503,
  'service-unavailable': 503,
};

/// `GET /v1/status`.
MultiplayerServiceFeatures? readStatusAnswer(List<int> bytes) =>
    _read(bytes, ControlPlaneBounds.small, (root) {
      if (_text(root, 'schema') != _statusSchema ||
          _integer(root, 'api', 1, 1) != 1) {
        return null;
      }
      final features = _object(root, 'features');
      final hosting = _object(root, 'hosting');
      final relay = _object(root, 'relay');
      final heartbeat = _integer(hosting, 'heartbeatSeconds', 5, 300);
      return MultiplayerServiceFeatures(
        hosting: _flag(features, 'hosting'),
        invites: _flag(features, 'invites'),
        signaling: _flag(features, 'signaling'),
        relay: _flag(features, 'relay'),
        killSwitch: _flag(root, 'killSwitch'),
        heartbeat: Duration(seconds: heartbeat),
        listingTtl: Duration(
          seconds: _integer(hosting, 'listingTtlSeconds', heartbeat + 1, 3600),
        ),
        relayCredentialTtl: Duration(
          seconds: _integer(relay, 'credentialTtlSeconds', 60, 86400),
        ),
      );
    });

/// `GET /v1/servers`: a page for [filter], after [after] when it is set.
///
/// Every listing must be a public one of the filter's build and protocol
/// major, and the page must move forward: server ids rise, all past [after],
/// and `next`, when there is one, is the page's last server id. So a page
/// can never lead back to itself or to a page already shown.
ServerListPage? readServerPage(
  List<int> bytes,
  ServerBrowseFilter filter,
  String? after,
) => _read(bytes, ControlPlaneBounds.page, (root) {
  final servers = _list(root, 'servers');
  final next = _optionalText(root, 'next');
  if (servers.length > filter.pageSize) return null;
  final listings = <ServerListing>[];
  var previous = after;
  for (final item in servers) {
    if (item is! Map<String, Object?>) return null;
    final listing = _listing(item, filter: filter);
    if (previous != null && listing.serverId.compareTo(previous) <= 0) {
      return null;
    }
    previous = listing.serverId;
    listings.add(listing);
  }
  if (next != null && (listings.isEmpty || next != listings.last.serverId)) {
    return null;
  }
  return ServerListPage(List.unmodifiable(listings), next);
});

/// `POST /v1/invites/resolve` or `GET /v1/servers/{serverId}`. An invite's
/// answer names its server; a detail answer is for [serverId], the id asked
/// for, and any server it names must be that one.
ServerAnswer? readServerAnswer(
  List<int> bytes, {
  String? serverId,
}) => _read(bytes, ControlPlaneBounds.server, (root) {
  final named = root.containsKey('serverId') ? _text(root, 'serverId') : null;
  final id = serverId ?? named;
  if (id == null || !WireText.isHex(id, 32)) return null;
  if (named != null && named != id) return null;
  final sha = _text(root, 'descriptorSha256');
  if (!WireText.isHex(sha, 32)) return null;
  final descriptor = Uint8List.fromList(utf8.encode(_text(root, 'descriptor')));
  if (descriptor.isEmpty ||
      descriptor.length > SignedServerDescriptorV1.maximumBytes) {
    return null;
  }
  final listing = _object(root, 'listing');
  if (_text(listing, 'schema') != _listingSchema ||
      _text(listing, 'serverId') != id ||
      _text(listing, 'descriptorSha256') != sha) {
    return null;
  }
  final visibility = _text(listing, 'visibility');
  if (visibility != 'public' && visibility != 'invite') return null;
  final maxPlayers = _integer(
    listing,
    'maxPlayers',
    ServerDescriptorV1.minimumPlayers,
    ServerDescriptorV1.maximumPlayers,
  );
  return ServerAnswer._(
    serverId: id,
    descriptorBytes: descriptor.asUnmodifiableView(),
    descriptorSha256: sha,
    players: _integer(listing, 'players', 0, maxPlayers),
    hostRelayOnly: _flag(listing, 'relayOnly'),
  );
});

/// The service's own error code in an error answer with [status], when the
/// code is one its catalogue gives that status; null for anything else, such
/// as a page from a platform in front of the service. The message is never
/// read, since it is text this launcher did not write.
String? readErrorCode(int status, List<int> bytes) {
  try {
    final root = CanonicalJson.decode(
      bytes,
      maximumBytes: ControlPlaneBounds.small,
    );
    if (root is! Map<String, Object?>) return null;
    final error = root['error'];
    if (error is! Map<String, Object?>) return null;
    final code = error['code'];
    if (code is! String || error['message'] is! String) return null;
    return _catalogue[code] == status ? code : null;
  } on FormatException {
    return null;
  }
}

/// One public listing of [filter]'s build and protocol major.
ServerListing _listing(
  Map<String, Object?> item, {
  required ServerBrowseFilter filter,
}) {
  if (_text(item, 'schema') != _listingSchema ||
      _text(item, 'visibility') != 'public') {
    throw const FormatException('Not a public listing.');
  }
  final protocolVersion = _text(item, 'protocolVersion');
  if (!ServerDescriptorV1.isSemanticVersion(protocolVersion) ||
      protocolVersion.substring(0, protocolVersion.indexOf('.')) !=
          '${filter.protocolMajor}' ||
      _text(item, 'gameBuild') != filter.gameBuild) {
    throw const FormatException('A listing of another build or protocol.');
  }
  if (!WireText.isHex(_text(item, 'packageDigest'), 32)) {
    throw const FormatException('A malformed package digest.');
  }
  _integer(item, 'packageCount', 0, ServerDescriptorV1.maximumPackages);
  _integer(item, 'updatedAt', 0, _latestTime);
  _flag(item, 'hostServesPackages');
  _flag(item, 'relayOnly');
  final maxPlayers = _integer(
    item,
    'maxPlayers',
    ServerDescriptorV1.minimumPlayers,
    ServerDescriptorV1.maximumPlayers,
  );
  // The constructor checks the id, hash, names, build and runtime version,
  // and throws ArgumentError for any it refuses.
  return ServerListing(
    serverId: _text(item, 'serverId'),
    name: _text(item, 'name'),
    source: ServerListingSource.internet,
    players: _integer(item, 'players', 0, maxPlayers),
    maxPlayers: maxPlayers,
    passwordRequired: _flag(item, 'passwordRequired'),
    voice: _flag(item, 'voice'),
    gamemode: _optionalText(item, 'gamemode'),
    world: _optionalText(item, 'world'),
    gameBuild: filter.gameBuild,
    runtimeVersion: _text(item, 'runtimeVersion'),
    descriptorSha256: _text(item, 'descriptorSha256'),
  );
}

T? _read<T>(
  List<int> bytes,
  int maximum,
  T? Function(Map<String, Object?> root) read,
) {
  try {
    final root = CanonicalJson.decode(bytes, maximumBytes: maximum);
    return root is Map<String, Object?> ? read(root) : null;
  } on FormatException {
    return null;
  } on ArgumentError {
    return null;
  }
}

Map<String, Object?> _object(Map<String, Object?> value, String key) =>
    switch (value[key]) {
      final Map<String, Object?> inner => inner,
      _ => throw FormatException('$key is not an object.'),
    };

List<Object?> _list(Map<String, Object?> value, String key) =>
    switch (value[key]) {
      final List<Object?> items => items,
      _ => throw FormatException('$key is not a list.'),
    };

String _text(Map<String, Object?> value, String key) => switch (value[key]) {
  final String text => text,
  _ => throw FormatException('$key is not text.'),
};

/// Text or null, where the member must be present either way.
String? _optionalText(Map<String, Object?> value, String key) {
  if (!value.containsKey(key)) throw FormatException('$key is missing.');
  return switch (value[key]) {
    null => null,
    final String text => text,
    _ => throw FormatException('$key is not text.'),
  };
}

bool _flag(Map<String, Object?> value, String key) => switch (value[key]) {
  final bool flag => flag,
  _ => throw FormatException('$key is not true or false.'),
};

int _integer(Map<String, Object?> value, String key, int minimum, int maximum) {
  final number = value[key];
  if (number is! int || number < minimum || number > maximum) {
    throw FormatException('$key is out of range.');
  }
  return number;
}
