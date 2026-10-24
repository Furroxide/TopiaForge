/// Where the TopiaForge multiplayer service is, and what the launcher does
/// while it has none.
///
/// The service's origin comes from the `TOPIAFORGE_CONTROL_PLANE_ORIGIN`
/// dart-define. Builds without it contact no service at all: Internet
/// browsing, invites, server links and Internet listings report that the
/// service is unavailable, while LAN and direct play work as before. Builds
/// that name one talk to it through the HTTPS client in
/// `control_plane_client.dart`.
library;

import 'package:launcher_domain/launcher_domain.dart';

import 'control_plane_results.dart';

export 'package:launcher_domain/launcher_domain.dart'
    show MultiplayerServiceStatus;

export 'control_plane_client.dart' show ControlPlaneLog, openControlPlane;
export 'control_plane_results.dart';

/// The multiplayer service this build uses.
final class MultiplayerServiceConfiguration {
  const MultiplayerServiceConfiguration._(this.status, this.origin);

  /// No service: the default.
  static const none = MultiplayerServiceConfiguration._(
    MultiplayerServiceStatus.unconfigured,
    null,
  );

  /// The dart-define that names the service's origin.
  static const defineName = 'TOPIAFORGE_CONTROL_PLANE_ORIGIN';

  /// The configuration compiled into this build.
  factory MultiplayerServiceConfiguration.fromEnvironment() =>
      MultiplayerServiceConfiguration.parse(
        const String.fromEnvironment(defineName),
      );

  /// Reads an origin such as `https://play.example`: HTTPS, a DNS name, an
  /// optional port of 1 to 65535 in plain decimal, at most a trailing `/`,
  /// and nothing else. Empty text is no service; anything else that is not
  /// such an origin is [MultiplayerServiceStatus.invalid].
  ///
  /// The [origin] is the one spelling the launch request carries: lowercase,
  /// with no trailing `/` and no default port. The launch request's own rule,
  /// [LaunchSessionRequest.isServiceOrigin], then decides whether it is
  /// accepted, so a build is configured only with an origin every launch can
  /// carry. That rule refuses an address in any spelling: an IP literal,
  /// bracketed or not, and a name whose last label is a number, decimal or
  /// `0x` hexadecimal, which the URL Standard reads as an IPv4 address:
  /// `https://0x7f000001` is 127.0.0.1, and `https://example.123` is no valid
  /// URL at all.
  factory MultiplayerServiceConfiguration.parse(String text) {
    final trimmed = text.trim();
    if (trimmed.isEmpty) return none;
    final origin = _tryParseOrigin(trimmed);
    if (origin == null) {
      return const MultiplayerServiceConfiguration._(
        MultiplayerServiceStatus.invalid,
        null,
      );
    }
    return MultiplayerServiceConfiguration._(
      MultiplayerServiceStatus.configured,
      origin,
    );
  }

  /// The origin [text] names, or null. The text is read as written rather
  /// than through [Uri.parse], which decodes escapes in a host and takes a
  /// port of any size.
  static Uri? _tryParseOrigin(String text) {
    const scheme = 'https://';
    if (text.length > 256 || !text.toLowerCase().startsWith(scheme)) {
      return null;
    }
    var authority = text.substring(scheme.length);
    if (authority.endsWith('/')) {
      authority = authority.substring(0, authority.length - 1);
    }
    // A path, query, fragment or credentials make it more than an origin,
    // and brackets hold an IPv6 address.
    if (authority.isEmpty || authority.contains(RegExp(r'[/?#@\[\]\\\s]'))) {
      return null;
    }
    final colon = authority.indexOf(':');
    final host = colon < 0 ? authority : authority.substring(0, colon);
    final port = colon < 0
        ? null
        : _tryParsePort(authority.substring(colon + 1));
    if (colon >= 0 && port == null) return null;
    final name = NetworkHost.tryNormalize(host);
    if (name == null) return null;
    final origin = Uri(scheme: 'https', host: name, port: port);
    // One rule decides which origins a build may name and which a launch
    // carries: the launch request's, read on the spelling a launch carries.
    return LaunchSessionRequest.isServiceOrigin('$origin') ? origin : null;
  }

  /// One to five digits without a leading zero, from 1 to 65535.
  static int? _tryParsePort(String text) {
    if (!RegExp(r'^[1-9][0-9]{0,4}$').hasMatch(text)) return null;
    final port = int.parse(text);
    return port <= 65535 ? port : null;
  }

  final MultiplayerServiceStatus status;

  /// The service's origin when [status] is configured. It names the
  /// project's own service, never a player.
  final Uri? origin;

  bool get isConfigured => status == MultiplayerServiceStatus.configured;
}

/// One page of the Internet server browser, or why there is none.
final class InternetBrowseResult {
  InternetBrowseResult.page(List<ServerListing> listings, {this.next})
    : listings = List.unmodifiable(listings),
      unavailableMessage = null,
      problem = null,
      retryAfter = null;

  const InternetBrowseResult.unavailable(
    String message, {
    this.problem = MultiplayerServiceProblem.notConfigured,
    this.retryAfter,
  }) : listings = const [],
       next = null,
       unavailableMessage = message;

  /// The page's servers, in server id order. Each names its server by id;
  /// nothing in one is trusted until that server's descriptor verifies.
  final List<ServerListing> listings;

  /// The cursor for the next page, when there is one.
  final String? next;

  /// A sentence for the player when the browser could not load.
  final String? unavailableMessage;

  /// Why the browser could not load.
  final MultiplayerServiceProblem? problem;

  /// How long to wait before asking again, when that is known.
  final Duration? retryAfter;

  bool get isAvailable => unavailableMessage == null;
}

/// What the launcher asks of the multiplayer service. A build that names a
/// service uses the HTTPS client (`openControlPlane`); any other build uses
/// [UnavailableControlPlane], which contacts nothing.
///
/// It reads the service's status, lists public servers, and answers the join
/// stages that go through the service: resolving an invite, a server link or
/// an Internet listing, and fetching such a target's descriptor, which comes
/// from the service and never from the host. It never contacts a host.
abstract interface class ControlPlaneClient {
  MultiplayerServiceStatus get status;

  /// What the service offers right now. Throws
  /// [OperationCancelledException] when [cancellation] stops it, and nothing
  /// else.
  Future<MultiplayerServiceCheck> readStatus({CancellationToken? cancellation});

  /// The first page of public servers [filter] selects, or the page after
  /// [after], a server id a page named as its `next`. Throws
  /// [OperationCancelledException] when [cancellation] stops it, and nothing
  /// else.
  Future<InternetBrowseResult> browse(
    ServerBrowseFilter filter, {
    String? after,
    CancellationToken? cancellation,
  });

  /// The public server [serverId], with its descriptor verified, for a
  /// browser's detail pane. Throws [OperationCancelledException] when
  /// [cancellation] stops it, and nothing else.
  Future<InternetServerLookup> fetchServer(
    String serverId, {
    CancellationToken? cancellation,
  });

  /// Answers a join in [JoinStage.resolving]; null when cancelled.
  Future<JoinEvent?> resolve(
    JoinTarget target, {
    CancellationToken? cancellation,
  });

  /// Answers [request] for a target the service resolves; null when
  /// cancelled.
  Future<JoinEvent?> fetchDescriptor(
    JoinDescriptorRequest request, {
    CancellationToken? cancellation,
  });
}

/// The service as a build without one sees it: everything is unavailable,
/// immediately, and nothing is contacted.
final class UnavailableControlPlane implements ControlPlaneClient {
  const UnavailableControlPlane([
    this.status = MultiplayerServiceStatus.unconfigured,
  ]);

  @override
  final MultiplayerServiceStatus status;

  /// Why the service cannot be used, for the player.
  String get message => switch (status) {
    MultiplayerServiceStatus.unconfigured =>
      'Internet play is not available in this build, because no '
          'multiplayer service is configured. LAN and direct play still work.',
    MultiplayerServiceStatus.invalid =>
      'Internet play is not available, because the multiplayer service '
          'configured in this build is not valid. LAN and direct play still '
          'work.',
    MultiplayerServiceStatus.configured =>
      'Internet play is not available in this version of the launcher yet. '
          'LAN and direct play still work.',
  };

  @override
  Future<MultiplayerServiceCheck> readStatus({
    CancellationToken? cancellation,
  }) async {
    cancellation?.throwIfCancelled();
    return MultiplayerServiceCheck.unavailable(
      MultiplayerServiceProblem.notConfigured,
      message,
    );
  }

  @override
  Future<InternetBrowseResult> browse(
    ServerBrowseFilter filter, {
    String? after,
    CancellationToken? cancellation,
  }) async {
    cancellation?.throwIfCancelled();
    return InternetBrowseResult.unavailable(message);
  }

  @override
  Future<InternetServerLookup> fetchServer(
    String serverId, {
    CancellationToken? cancellation,
  }) async {
    cancellation?.throwIfCancelled();
    return InternetServerLookup.failed(
      MultiplayerServiceProblem.notConfigured,
      message,
    );
  }

  @override
  Future<JoinEvent?> resolve(
    JoinTarget target, {
    CancellationToken? cancellation,
  }) async => _unavailable(cancellation);

  @override
  Future<JoinEvent?> fetchDescriptor(
    JoinDescriptorRequest request, {
    CancellationToken? cancellation,
  }) async => _unavailable(cancellation);

  JoinEvent? _unavailable(CancellationToken? cancellation) =>
      cancellation?.isCancelled ?? false
      ? null
      : JoinFailed(JoinFailureCode.serviceUnavailable, detail: message);
}
