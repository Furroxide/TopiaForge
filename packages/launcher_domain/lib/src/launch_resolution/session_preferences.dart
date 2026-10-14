part of '../launch_resolution.dart';

/// What a host opens by default: the host dialog's starting values and the
/// in-game "Open to multiplayer" settings.
final class MultiplayerHostingDefaults {
  MultiplayerHostingDefaults({
    required String name,
    int maxPlayers = 8,
    this.visibility = ServerVisibility.lan,
    int port = LanBeacon.defaultGamePort,
    this.voice = false,
  }) : name = _displayText(name, ServerDescriptorV1.maximumNameLength),
       maxPlayers = _ranged(
         maxPlayers,
         ServerDescriptorV1.minimumPlayers,
         ServerDescriptorV1.maximumPlayers,
       ),
       port = _ranged(port, 0, 65535);

  /// The defaults for a player called [displayName]: `<name>'s server`.
  factory MultiplayerHostingDefaults.forPlayer(String displayName) =>
      MultiplayerHostingDefaults(name: "$displayName's server");

  factory MultiplayerHostingDefaults.fromJson(Object? value) {
    const members = {'maxPlayers', 'name', 'port', 'visibility', 'voice'};
    final json = _object(value, members, members);
    return MultiplayerHostingDefaults(
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
      port: _ranged(json['port'], 0, 65535),
      voice: _boolean(json['voice']),
    );
  }

  final String name;
  final int maxPlayers;
  final ServerVisibility visibility;

  /// The port to host on; zero picks any free port.
  final int port;
  final bool voice;

  Map<String, Object?> toJson() => {
    'maxPlayers': maxPlayers,
    'name': name,
    'port': port,
    'visibility': visibility.wireName,
    'voice': voice,
  };
}

/// The launcher's multiplayer preferences as the game reads them
/// (`multiplayer-preferences-v1.json` in manager staging, launcher to game).
///
/// The launcher owns these preferences and rewrites the projection whenever
/// they change. The game never edits it: it records changes made in game in
/// a [MultiplayerPreferencesJournal], which the launcher applies
/// ([MultiplayerPreferencesJournal.applyTo]) before writing the next
/// [revision]. [journalApplied] is the last journal sequence applied, so the
/// game can drop what the launcher has taken in.
///
/// Privacy is always relay-only here: allowing direct play is a choice for
/// one session, never a stored default. The projection holds no address.
final class MultiplayerPreferencesProjection {
  MultiplayerPreferencesProjection({
    required int revision,
    int journalApplied = 0,
    required String displayName,
    required int disclosure,
    required int voiceConsent,
    required this.voiceEnabled,
    required this.lanDiscovery,
    required this.upnp,
    required this.hosting,
    String? serviceOrigin,
  }) : revision = _integer(revision),
       journalApplied = _integer(journalApplied),
       displayName = _displayText(
         displayName,
         MultiplayerSettings.maximumDisplayNameLength,
         minimum: MultiplayerSettings.minimumDisplayNameLength,
       ),
       disclosure = _integer(disclosure),
       voiceConsent = _integer(voiceConsent),
       serviceOrigin = serviceOrigin == null
           ? null
           : _serviceOrigin(serviceOrigin) {
    if (voiceEnabled && this.voiceConsent < 1) {
      throw const FormatException('Voice stays off until the player consents.');
    }
  }

  static const schemaVersion = 1;

  /// The projection of [settings] with [hosting] defaults and the
  /// configured [serviceOrigin].
  factory MultiplayerPreferencesProjection.fromSettings(
    MultiplayerSettings settings, {
    required MultiplayerHostingDefaults hosting,
    required int revision,
    int journalApplied = 0,
    String? serviceOrigin,
  }) => MultiplayerPreferencesProjection(
    revision: revision,
    journalApplied: journalApplied,
    displayName: settings.displayName,
    disclosure: settings.acknowledgedPrivacyDisclosure,
    voiceConsent: settings.voiceConsent,
    voiceEnabled: settings.voiceActive,
    lanDiscovery: settings.lanDiscoveryEnabled,
    upnp: settings.upnpEnabled,
    hosting: hosting,
    serviceOrigin: serviceOrigin,
  );

  factory MultiplayerPreferencesProjection.fromJson(Object? value) {
    const required = {
      'displayName',
      'hosting',
      'journalApplied',
      'network',
      'privacy',
      'revision',
      'schemaVersion',
      'voice',
    };
    final json = _object(value, {...required, 'serviceOrigin'}, required);
    if (_integer(json['schemaVersion']) != schemaVersion) {
      throw const FormatException('Unsupported multiplayer preferences.');
    }
    final privacy = _object(
      json['privacy'],
      {'disclosure', 'mode'},
      {'disclosure', 'mode'},
    );
    if (_text(privacy['mode']) !=
        MultiplayerAddressPrivacy.relayOnly.wireName) {
      throw const FormatException('Direct play is never a stored default.');
    }
    final voice = _object(
      json['voice'],
      {'consent', 'enabled'},
      {'consent', 'enabled'},
    );
    final network = _object(json['network'], {'lan', 'upnp'}, {'lan', 'upnp'});
    return MultiplayerPreferencesProjection(
      revision: _integer(json['revision']),
      journalApplied: _integer(json['journalApplied']),
      displayName: _text(json['displayName'], max: 64),
      disclosure: _integer(privacy['disclosure']),
      voiceConsent: _integer(voice['consent']),
      voiceEnabled: _boolean(voice['enabled']),
      lanDiscovery: _boolean(network['lan']),
      upnp: _boolean(network['upnp']),
      hosting: MultiplayerHostingDefaults.fromJson(json['hosting']),
      serviceOrigin: json.containsKey('serviceOrigin')
          ? _serviceOrigin(json['serviceOrigin'])
          : null,
    );
  }

  final int revision;
  final int journalApplied;
  final String displayName;

  /// The address disclosure version the player acknowledged; 0 when never.
  final int disclosure;

  /// The microphone consent version the player gave; 0 when never.
  final int voiceConsent;
  final bool voiceEnabled;
  final bool lanDiscovery;
  final bool upnp;
  final MultiplayerHostingDefaults hosting;
  final String? serviceOrigin;

  /// [settings] with the preferences the game may change applied: the
  /// display name, the acknowledged disclosure, voice, LAN discovery and
  /// UPnP. Favorites, history and link handling stay as they are.
  MultiplayerSettings applyTo(MultiplayerSettings settings) =>
      MultiplayerSettings(
        displayName: displayName,
        acknowledgedPrivacyDisclosure: disclosure,
        upnpEnabled: upnp,
        lanDiscoveryEnabled: lanDiscovery,
        voiceConsent: voiceConsent,
        voiceEnabled:
            voiceEnabled &&
            voiceConsent >= MultiplayerSettings.currentVoiceConsent,
        linkHandlingEnabled: settings.linkHandlingEnabled,
        favorites: settings.favorites,
        history: settings.history,
      );

  MultiplayerPreferencesProjection _with({
    required int revision,
    required int journalApplied,
    String? displayName,
    int? disclosure,
    int? voiceConsent,
    bool? voiceEnabled,
    bool? lanDiscovery,
    bool? upnp,
    MultiplayerHostingDefaults? hosting,
  }) => MultiplayerPreferencesProjection(
    revision: revision,
    journalApplied: journalApplied,
    displayName: displayName ?? this.displayName,
    disclosure: disclosure ?? this.disclosure,
    voiceConsent: voiceConsent ?? this.voiceConsent,
    voiceEnabled: voiceEnabled ?? this.voiceEnabled,
    lanDiscovery: lanDiscovery ?? this.lanDiscovery,
    upnp: upnp ?? this.upnp,
    hosting: hosting ?? this.hosting,
    serviceOrigin: serviceOrigin,
  );

  Map<String, Object?> toJson() =>
      _sorted({
            'displayName': displayName,
            'hosting': hosting.toJson(),
            'journalApplied': journalApplied,
            'network': {'lan': lanDiscovery, 'upnp': upnp},
            'privacy': {
              'disclosure': disclosure,
              'mode': MultiplayerAddressPrivacy.relayOnly.wireName,
            },
            'revision': revision,
            'schemaVersion': schemaVersion,
            if (serviceOrigin != null) 'serviceOrigin': serviceOrigin,
            'voice': {'consent': voiceConsent, 'enabled': voiceEnabled},
          })!
          as Map<String, Object?>;
}
