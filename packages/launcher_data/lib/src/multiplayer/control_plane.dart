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

  /// Reads an origin such as `https://play.example`: HTTPS, a host, an
  /// optional port, and nothing else. Empty text is no service; anything
  /// else that is not such an origin is [MultiplayerServiceStatus.invalid].
  factory MultiplayerServiceConfiguration.parse(String text) {
    final trimmed = text.trim();
    if (trimmed.isEmpty) return none;
    final uri = Uri.tryParse(trimmed);
    final valid =
        uri != null &&
        trimmed.length <= 256 &&
        uri.scheme == 'https' &&
        uri.host.isNotEmpty &&
        uri.userInfo.isEmpty &&
        (uri.path.isEmpty || uri.path == '/') &&
        !uri.hasQuery &&
        !uri.hasFragment;
    if (!valid) {
      return const MultiplayerServiceConfiguration._(
        MultiplayerServiceStatus.invalid,
        null,
      );
    }
    return MultiplayerServiceConfiguration._(
      MultiplayerServiceStatus.configured,
      Uri(scheme: 'https', host: uri.host, port: uri.hasPort ? uri.port : null),
    );
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
