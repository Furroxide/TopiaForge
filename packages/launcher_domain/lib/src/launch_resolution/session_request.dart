part of '../launch_resolution.dart';

/// Whether a session launch hosts or joins.
enum LaunchSessionRole {
  host('host-session'),
  join('join-session');

  const LaunchSessionRole(this.command);

  /// The launch command a request for this role carries.
  final String command;
}

/// The launch commands only request V5 carries.
const sessionLaunchCommands = ['host-session', 'join-session'];

/// The player's explicit consent to direct play for one session, which
/// [MultiplayerAddressPrivacy.direct] requires. It names the session it was
/// given for and the address disclosure the player had acknowledged, so a
/// consent can neither carry over from another session nor predate the
/// disclosure. Relay-only sessions never carry one.
final class LaunchDirectConsent {
  LaunchDirectConsent({required String intentId, required int disclosure})
    : intentId = _token(intentId),
      disclosure = _ranged(disclosure, 1, 2147483647);

  factory LaunchDirectConsent.fromJson(Object? value) {
    final json = _object(
      value,
      {'disclosure', 'intentId'},
      {'disclosure', 'intentId'},
    );
    return LaunchDirectConsent(
      intentId: _token(json['intentId']),
      disclosure: _ranged(json['disclosure'], 1, 2147483647),
    );
  }

  /// The session intent the player allowed direct play for.
  final String intentId;

  /// The address disclosure version the player had acknowledged.
  final int disclosure;

  Map<String, Object?> toJson() => {
    'disclosure': disclosure,
    'intentId': intentId,
  };
}

/// How the session's transport may reach peers, and its local port.
final class LaunchSessionTransport {
  LaunchSessionTransport._({
    required this.privacy,
    required this.directConsent,
    required int port,
    required this.upnp,
    required this.lan,
  }) : port = _ranged(port, 0, 65535);

  factory LaunchSessionTransport._fromJson(Object? value) {
    final json = _object(
      value,
      {'directConsent', 'lan', 'port', 'privacy', 'upnp'},
      {'lan', 'port', 'privacy', 'upnp'},
    );
    return LaunchSessionTransport._(
      privacy: _named(
        json['privacy'],
        MultiplayerAddressPrivacy.values,
        (item) => item.wireName,
      ),
      directConsent: json.containsKey('directConsent')
          ? LaunchDirectConsent.fromJson(json['directConsent'])
          : null,
      port: _ranged(json['port'], 0, 65535),
      upnp: _boolean(json['upnp']),
      lan: _boolean(json['lan']),
    );
  }

  /// The privacy the player chose for this session before it started.
  final MultiplayerAddressPrivacy privacy;

  /// Present exactly when [privacy] is direct.
  final LaunchDirectConsent? directConsent;

  /// The local game port: the port a host listens on, or a joining player's
  /// own. Zero picks any free port.
  final int port;

  /// Whether a host may map its port through UPnP. A join never maps one.
  final bool upnp;

  /// Whether LAN discovery is on: a host announces itself on the LAN, and a
  /// joining player may look for a LAN listing.
  final bool lan;

  bool get allowsDirect => privacy == MultiplayerAddressPrivacy.direct;

  Map<String, Object?> toJson() => {
    if (directConsent != null) 'directConsent': directConsent!.toJson(),
    'lan': lan,
    'port': port,
    'privacy': privacy.wireName,
    'upnp': upnp,
  };
}

/// What a host-session launch opens. The password of a protected session is
/// typed in game, so it is never staged: [passwordRequired] only tells the
/// game to ask for one before it opens the session.
final class LaunchHostOptions {
  LaunchHostOptions({
    required String name,
    int maxPlayers = 8,
    this.visibility = ServerVisibility.lan,
    this.voice = false,
    this.passwordRequired = false,
    this.servePackages = false,
  }) : name = _displayText(name, ServerDescriptorV1.maximumNameLength),
       maxPlayers = _ranged(
         maxPlayers,
         ServerDescriptorV1.minimumPlayers,
         ServerDescriptorV1.maximumPlayers,
       );

  factory LaunchHostOptions.fromJson(Object? value) {
    const members = {
      'maxPlayers',
      'name',
      'passwordRequired',
      'servePackages',
      'visibility',
      'voice',
    };
    final json = _object(value, members, members);
    return LaunchHostOptions(
      name: _displayText(json['name'], ServerDescriptorV1.maximumNameLength),
      maxPlayers: _ranged(
        json['maxPlayers'],
        ServerDescriptorV1.minimumPlayers,
        ServerDescriptorV1.maximumPlayers,
      ),
      visibility: _named(
        json['visibility'],
        ServerVisibility.values,
        (item) => item.wireName,
      ),
      voice: _boolean(json['voice']),
      passwordRequired: _boolean(json['passwordRequired']),
      servePackages: _boolean(json['servePackages']),
    );
  }

  /// The server name, 1 to 48 UTF-16 code units of display text.
  final String name;
  final int maxPlayers;
  final ServerVisibility visibility;
  final bool voice;
  final bool passwordRequired;

  /// Developer mode only ([LaunchSessionRequest.developerMode]): the host
  /// serves its package archives to joining players, who consent to them
  /// separately.
  final bool servePackages;

  Map<String, Object?> toJson() => {
    'maxPlayers': maxPlayers,
    'name': name,
    'passwordRequired': passwordRequired,
    'servePackages': servePackages,
    'visibility': visibility.wireName,
    'voice': voice,
  };
}

/// One peer of `topiaforge dev --peers N`: its slot among [count] peers.
final class LaunchDevInstance {
  LaunchDevInstance({required int index, required int count})
    : count = _ranged(count, 2, 8),
      index = _ranged(index, 0, 7) {
    if (this.index >= this.count) {
      throw const FormatException('A peer slot lies outside its peers.');
    }
  }

  factory LaunchDevInstance.fromJson(Object? value) {
    final json = _object(value, {'count', 'index'}, {'count', 'index'});
    return LaunchDevInstance(
      index: _ranged(json['index'], 0, 7),
      count: _ranged(json['count'], 2, 8),
    );
  }

  final int index;
  final int count;

  Map<String, Object?> toJson() => {'count': count, 'index': index};
}

/// The `session` object of launch request V5: who hosts or joins, under what
/// privacy, and where.
///
/// - The role agrees with the command: a host carries [host] and no
///   [target]; a join carries [target] and no [host].
/// - Direct privacy carries the player's per-session consent
///   ([LaunchSessionTransport.directConsent]), given for this [intentId].
///   Under relay-only, a direct target may only name a host on the local
///   network; any other lacks that consent and is refused.
/// - Invites, Internet listings, and public or invite-only hosting need the
///   control plane, so they need a [serviceOrigin]. LAN listings and LAN or
///   public hosting need LAN discovery on.
/// - Only [developerMode] lets a host serve its packages.
/// - Passwords are typed in game and never staged.
final class LaunchSessionRequest {
  LaunchSessionRequest._({
    required this.role,
    required String intentId,
    required String displayName,
    required this.transport,
    required this.developerMode,
    String? serviceOrigin,
    this.target,
    this.host,
    this.devInstance,
  }) : intentId = _token(intentId),
       displayName = _displayText(
         displayName,
         MultiplayerSettings.maximumDisplayNameLength,
         minimum: MultiplayerSettings.minimumDisplayNameLength,
       ),
       serviceOrigin = serviceOrigin == null
           ? null
           : _serviceOrigin(serviceOrigin) {
    const invalid = FormatException('Inconsistent launch session.');
    final consent = transport.directConsent;
    if ((role == LaunchSessionRole.host) != (host != null) ||
        (role == LaunchSessionRole.join) != (target != null) ||
        (role == LaunchSessionRole.join && transport.upnp) ||
        transport.allowsDirect != (consent != null) ||
        (consent != null && consent.intentId != this.intentId)) {
      throw invalid;
    }
    final service = this.serviceOrigin != null;
    switch (target) {
      case LaunchDirectTarget(onLocalNetwork: false)
          when !transport.allowsDirect:
        throw const FormatException(
          'A direct target beyond the local network needs consent to direct '
          'play.',
        );
      case LaunchListingTarget(source: ServerListingSource.lan)
          when !transport.lan:
        throw invalid;
      case LaunchSessionTarget(needsService: true) when !service:
        throw invalid;
      default:
        break;
    }
    final visibility = host?.visibility;
    if ((visibility == ServerVisibility.public ||
            visibility == ServerVisibility.lan) &&
        !transport.lan) {
      throw invalid;
    }
    if ((visibility == ServerVisibility.public ||
            visibility == ServerVisibility.invite) &&
        !service) {
      throw invalid;
    }
    if (host != null && host!.servePackages && !developerMode) {
      throw const FormatException('Only developer mode serves packages.');
    }
  }

  /// A host-session launch under the privacy the player chose for this
  /// session. A direct [SessionPrivacy] only comes from
  /// [MultiplayerSettings.privacyForSession], after the disclosure.
  factory LaunchSessionRequest.host({
    required String intentId,
    required MultiplayerSettings settings,
    required SessionPrivacy privacy,
    required LaunchHostOptions host,
    int port = LanBeacon.defaultGamePort,
    String? serviceOrigin,
    LaunchDevInstance? devInstance,
    bool developerMode = false,
  }) => LaunchSessionRequest._(
    role: LaunchSessionRole.host,
    intentId: intentId,
    displayName: settings.displayName,
    transport: _transportFor(
      intentId,
      settings,
      privacy,
      port: port,
      upnp: settings.upnpEnabled,
    ),
    developerMode: developerMode,
    serviceOrigin: serviceOrigin,
    host: host,
    devInstance: devInstance,
  );

  /// A join-session launch of [target] under the privacy the player chose
  /// for this session.
  factory LaunchSessionRequest.join({
    required String intentId,
    required MultiplayerSettings settings,
    required SessionPrivacy privacy,
    required LaunchSessionTarget target,
    String? serviceOrigin,
    LaunchDevInstance? devInstance,
    bool developerMode = false,
  }) => LaunchSessionRequest._(
    role: LaunchSessionRole.join,
    intentId: intentId,
    displayName: settings.displayName,
    transport: _transportFor(intentId, settings, privacy, port: 0, upnp: false),
    developerMode: developerMode,
    serviceOrigin: serviceOrigin,
    target: target,
    devInstance: devInstance,
  );

  /// Whether [origin] is a control-plane origin as a launch carries it: see
  /// [serviceOrigin]. The multiplayer service configuration reads the same.
  static bool isServiceOrigin(String origin) {
    try {
      _serviceOrigin(origin);
      return true;
    } on FormatException {
      return false;
    }
  }

  factory LaunchSessionRequest.fromJson(Object? value) {
    final json = _object(
      value,
      {
        'devInstance',
        'developerMode',
        'displayName',
        'host',
        'intentId',
        'role',
        'serviceOrigin',
        'target',
        'transport',
      },
      {'developerMode', 'displayName', 'intentId', 'role', 'transport'},
    );
    return LaunchSessionRequest._(
      role: _named(json['role'], LaunchSessionRole.values, (item) => item.name),
      intentId: _token(json['intentId']),
      displayName: _text(json['displayName'], max: 64),
      transport: LaunchSessionTransport._fromJson(json['transport']),
      developerMode: _boolean(json['developerMode']),
      serviceOrigin: json.containsKey('serviceOrigin')
          ? _serviceOrigin(json['serviceOrigin'])
          : null,
      target: json.containsKey('target')
          ? LaunchSessionTarget.fromJson(json['target'])
          : null,
      host: json.containsKey('host')
          ? LaunchHostOptions.fromJson(json['host'])
          : null,
      devInstance: json.containsKey('devInstance')
          ? LaunchDevInstance.fromJson(json['devInstance'])
          : null,
    );
  }

  final LaunchSessionRole role;

  /// The launcher's intent: one host or join attempt, kept across the
  /// restarts and re-plans it takes.
  final String intentId;

  /// The name other players see, 2 to 24 UTF-16 code units.
  final String displayName;
  final LaunchSessionTransport transport;

  /// The control plane's origin, or null when none is configured.
  final String? serviceOrigin;
  final LaunchSessionTarget? target;
  final LaunchHostOptions? host;
  final LaunchDevInstance? devInstance;

  /// Whether the launcher runs in developer mode, which alone allows a host
  /// to serve its packages ([LaunchHostOptions.servePackages]).
  final bool developerMode;

  String get command => role.command;

  Map<String, Object?> toJson() => {
    if (devInstance != null) 'devInstance': devInstance!.toJson(),
    'developerMode': developerMode,
    'displayName': displayName,
    if (host != null) 'host': host!.toJson(),
    'intentId': intentId,
    'role': role.name,
    if (serviceOrigin != null) 'serviceOrigin': serviceOrigin,
    if (target != null) 'target': target!.toJson(),
    'transport': transport.toJson(),
  };

  @override
  String toString() =>
      'launch session (${role.name}${target == null ? '' : ', $target'})';

  static LaunchSessionTransport _transportFor(
    String intentId,
    MultiplayerSettings settings,
    SessionPrivacy privacy, {
    required int port,
    required bool upnp,
  }) {
    if (privacy.allowsDirect && settings.needsPrivacyDisclosure) {
      throw StateError(
        'The address disclosure must be acknowledged before a direct session.',
      );
    }
    return LaunchSessionTransport._(
      privacy: privacy.addressPrivacy,
      directConsent: privacy.allowsDirect
          ? LaunchDirectConsent(
              intentId: intentId,
              disclosure: settings.acknowledgedPrivacyDisclosure,
            )
          : null,
      port: port,
      upnp: upnp,
      lan: settings.lanDiscoveryEnabled,
    );
  }
}
