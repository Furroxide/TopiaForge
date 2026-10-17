/// Where the TopiaForge multiplayer service is, and what the launcher does
/// while it has none.
///
/// The service's origin comes from the `TOPIAFORGE_CONTROL_PLANE_ORIGIN`
/// dart-define. Builds without it, which is every build until the service
/// ships, contact no service at all: Internet browsing, invites, server links
/// and Internet listings report that the service is unavailable, while LAN
/// and direct play work as before.
library;

import 'package:launcher_domain/launcher_domain.dart';

import '../cancellation_token.dart';

/// Whether this build names a multiplayer service.
enum MultiplayerServiceStatus {
  /// No origin was configured; nothing is ever contacted.
  unconfigured,

  /// The configured origin is not an absolute HTTPS origin, so it is
  /// ignored and nothing is contacted.
  invalid,

  /// An origin is configured.
  configured,
}

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
  /// and nothing else. An IP literal is refused, bracketed or not, as the
  /// launch request refuses it. Empty text is no service; anything else that
  /// is not such an origin is [MultiplayerServiceStatus.invalid].
  ///
  /// The [origin] is the one spelling the launch request carries: lowercase,
  /// with no trailing `/` and no default port, so any text read here as
  /// configured is an origin the launch request accepts.
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
    // A DNS name only: an IPv4 literal is refused here, and an IPv6 one never
    // gets this far.
    final name = NetworkHost.tryNormalize(host);
    if (name == null || NetworkHost.pathKindOf(name) != null) return null;
    return Uri(scheme: 'https', host: name, port: port);
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
      unavailableMessage = null;

  const InternetBrowseResult.unavailable(String message)
    : listings = const [],
      next = null,
      unavailableMessage = message;

  final List<ServerListing> listings;

  /// The cursor for the next page, when there is one.
  final String? next;

  /// A sentence for the player when the browser could not load.
  final String? unavailableMessage;

  bool get isAvailable => unavailableMessage == null;
}

/// What the launcher asks of the multiplayer service. The client that talks
/// to a configured service arrives in a later slice; until then every build
/// uses [UnavailableControlPlane].
///
/// It answers the join stages that go through the service: resolving an
/// invite, a server link or an Internet listing, and fetching such a
/// target's descriptor, which comes from the service and never from the
/// host. It never contacts a host.
abstract interface class ControlPlaneClient {
  MultiplayerServiceStatus get status;

  /// The first page of public servers, or the page after [after].
  Future<InternetBrowseResult> browse({
    String? after,
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
  Future<InternetBrowseResult> browse({
    String? after,
    CancellationToken? cancellation,
  }) async => InternetBrowseResult.unavailable(message);

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
