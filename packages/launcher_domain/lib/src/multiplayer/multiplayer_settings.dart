part of '../multiplayer.dart';

/// Whether a session may use a network path that reveals the player's
/// address.
enum MultiplayerAddressPrivacy {
  /// Internet paths go through the relay, so the host and other players never
  /// learn this player's address. A LAN join is direct on the local network
  /// by nature.
  relayOnly('relay-only'),

  /// Direct paths are allowed for one session, which reveals the player's
  /// address to the peers it connects to.
  direct('direct');

  const MultiplayerAddressPrivacy(this.wireName);

  final String wireName;
}

/// The player's multiplayer preferences, as the launcher keeps them.
///
/// Defaults follow the live-multiplayer plan: relay-only privacy, UPnP off,
/// LAN discovery on, voice off until the player consents, and link handling
/// on. Allowing a direct path is never a stored default. It is an explicit
/// choice for one session ([privacyForSession]), and only after the player
/// has seen the disclosure that explains it.
final class MultiplayerSettings {
  MultiplayerSettings({
    this.displayName = defaultDisplayName,
    this.acknowledgedPrivacyDisclosure = 0,
    this.upnpEnabled = false,
    this.lanDiscoveryEnabled = true,
    this.voiceConsent = 0,
    this.voiceEnabled = false,
    this.linkHandlingEnabled = true,
    ServerFavorites? favorites,
    ServerHistory? history,
  }) : favorites = favorites ?? ServerFavorites(),
       history = history ?? ServerHistory() {
    if (!isValidDisplayName(displayName)) {
      throw ArgumentError.value(
        displayName,
        'displayName',
        'A display name is $minimumDisplayNameLength to '
            '$maximumDisplayNameLength printable characters.',
      );
    }
    if (acknowledgedPrivacyDisclosure < 0 || voiceConsent < 0) {
      throw ArgumentError('Disclosure and consent versions are not negative.');
    }
    if (voiceEnabled && !hasVoiceConsent) {
      throw ArgumentError.value(
        voiceEnabled,
        'voiceEnabled',
        'Voice stays off until the player consents to microphone use.',
      );
    }
  }

  static const schemaVersion = 1;
  static const defaultDisplayName = 'Player';
  static const minimumDisplayNameLength = 2;
  static const maximumDisplayNameLength = 24;

  /// The version of the address-exposure disclosure the launcher shows.
  /// Raising it after the wording changes asks every player again.
  static const currentPrivacyDisclosure = 1;

  /// The version of the microphone consent the launcher asks for. Raising it
  /// withdraws earlier consent, so voice stays off until the player agrees
  /// to the new wording.
  static const currentVoiceConsent = 1;

  final String displayName;

  /// The newest disclosure version the player acknowledged; 0 when never.
  final int acknowledgedPrivacyDisclosure;
  final bool upnpEnabled;
  final bool lanDiscoveryEnabled;

  /// The microphone consent version the player gave; 0 when never.
  final int voiceConsent;

  /// The player's voice switch. It counts only with current consent; see
  /// [voiceActive].
  final bool voiceEnabled;
  final bool linkHandlingEnabled;
  final ServerFavorites favorites;
  final ServerHistory history;

  /// Display names follow the wire's display-text rules, with 2 to 24 UTF-16
  /// code units.
  static bool isValidDisplayName(String? name) => WireText.isDisplayText(
    name,
    maximumDisplayNameLength,
    minimum: minimumDisplayNameLength,
  );

  /// The privacy every session starts from. It is always relay-only.
  MultiplayerAddressPrivacy get defaultPrivacy =>
      MultiplayerAddressPrivacy.relayOnly;

  /// Whether the address-exposure disclosure must be shown before the player
  /// hosts or joins.
  bool get needsPrivacyDisclosure =>
      acknowledgedPrivacyDisclosure < currentPrivacyDisclosure;

  bool get hasVoiceConsent => voiceConsent >= currentVoiceConsent;

  /// Whether voice is on: switched on, with consent to the current wording.
  bool get voiceActive => voiceEnabled && hasVoiceConsent;

  /// The privacy for one session. A direct path needs the player's explicit
  /// choice for this session and an acknowledged disclosure; the choice is
  /// never stored.
  MultiplayerAddressPrivacy privacyForSession({required bool allowDirect}) {
    if (!allowDirect) return MultiplayerAddressPrivacy.relayOnly;
    if (needsPrivacyDisclosure) {
      throw StateError(
        'The address disclosure must be acknowledged before a direct session.',
      );
    }
    return MultiplayerAddressPrivacy.direct;
  }

  MultiplayerSettings withDisplayName(String name) => _copy(displayName: name);

  MultiplayerSettings acknowledgePrivacyDisclosure() =>
      _copy(acknowledgedPrivacyDisclosure: currentPrivacyDisclosure);

  MultiplayerSettings withUpnp(bool enabled) => _copy(upnpEnabled: enabled);

  MultiplayerSettings withLanDiscovery(bool enabled) =>
      _copy(lanDiscoveryEnabled: enabled);

  MultiplayerSettings giveVoiceConsent() =>
      _copy(voiceConsent: currentVoiceConsent);

  /// Withdraws microphone consent, which also switches voice off.
  MultiplayerSettings withdrawVoiceConsent() =>
      _copy(voiceConsent: 0, voiceEnabled: false);

  /// Switches voice; switching it on needs current consent.
  MultiplayerSettings withVoice(bool enabled) => _copy(voiceEnabled: enabled);

  MultiplayerSettings withLinkHandling(bool enabled) =>
      _copy(linkHandlingEnabled: enabled);

  MultiplayerSettings withFavorites(ServerFavorites favorites) =>
      _copy(favorites: favorites);

  MultiplayerSettings withHistory(ServerHistory history) =>
      _copy(history: history);

  Map<String, Object?> toJson() => {
    'schemaVersion': schemaVersion,
    'displayName': displayName,
    'acknowledgedPrivacyDisclosure': acknowledgedPrivacyDisclosure,
    'upnpEnabled': upnpEnabled,
    'lanDiscoveryEnabled': lanDiscoveryEnabled,
    'voiceConsent': voiceConsent,
    'voiceEnabled': voiceEnabled,
    'linkHandlingEnabled': linkHandlingEnabled,
    'favorites': favorites.toJson(),
    'history': history.toJson(),
  };

  /// Reads stored settings. Missing members take their defaults and unknown
  /// members are ignored, so nothing stored can make a direct path a default.
  /// Voice switched on without current consent reads as off. Throws
  /// [FormatException] for a member of the wrong type or value.
  factory MultiplayerSettings.fromJson(Map<String, Object?> json) {
    final version = json['schemaVersion'];
    if (version != null && version != schemaVersion) {
      throw const FormatException('Unsupported multiplayer settings version.');
    }
    T read<T>(String key, T fallback) {
      if (!json.containsKey(key)) return fallback;
      final value = json[key];
      if (value is T) return value;
      throw FormatException("Multiplayer setting '$key' has the wrong type.");
    }

    final consent = read<int>('voiceConsent', 0);
    try {
      return MultiplayerSettings(
        displayName: read<String>('displayName', defaultDisplayName),
        acknowledgedPrivacyDisclosure: read<int>(
          'acknowledgedPrivacyDisclosure',
          0,
        ),
        upnpEnabled: read<bool>('upnpEnabled', false),
        lanDiscoveryEnabled: read<bool>('lanDiscoveryEnabled', true),
        voiceConsent: consent,
        voiceEnabled:
            read<bool>('voiceEnabled', false) && consent >= currentVoiceConsent,
        linkHandlingEnabled: read<bool>('linkHandlingEnabled', true),
        favorites: json.containsKey('favorites')
            ? ServerFavorites.fromJson(json['favorites'])
            : null,
        history: json.containsKey('history')
            ? ServerHistory.fromJson(json['history'])
            : null,
      );
    } on ArgumentError catch (error) {
      throw FormatException(
        'Multiplayer settings are invalid: ${error.message}',
      );
    }
  }

  MultiplayerSettings _copy({
    String? displayName,
    int? acknowledgedPrivacyDisclosure,
    bool? upnpEnabled,
    bool? lanDiscoveryEnabled,
    int? voiceConsent,
    bool? voiceEnabled,
    bool? linkHandlingEnabled,
    ServerFavorites? favorites,
    ServerHistory? history,
  }) => MultiplayerSettings(
    displayName: displayName ?? this.displayName,
    acknowledgedPrivacyDisclosure:
        acknowledgedPrivacyDisclosure ?? this.acknowledgedPrivacyDisclosure,
    upnpEnabled: upnpEnabled ?? this.upnpEnabled,
    lanDiscoveryEnabled: lanDiscoveryEnabled ?? this.lanDiscoveryEnabled,
    voiceConsent: voiceConsent ?? this.voiceConsent,
    voiceEnabled: voiceEnabled ?? this.voiceEnabled,
    linkHandlingEnabled: linkHandlingEnabled ?? this.linkHandlingEnabled,
    favorites: favorites ?? this.favorites,
    history: history ?? this.history,
  );
}
