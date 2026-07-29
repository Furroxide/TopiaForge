part of '../models.dart';

enum ModMultiplayerMode {
  clientLocal('client-local'),
  serverOnly('server-only'),
  session('session');

  const ModMultiplayerMode(this.wireName);

  final String wireName;

  static ModMultiplayerMode? tryParse(String value) {
    for (final mode in values) {
      if (mode.wireName == value) return mode;
    }
    return null;
  }
}

enum ModMultiplayerPresence {
  required('required'),
  optional('optional');

  const ModMultiplayerPresence(this.wireName);

  final String wireName;

  static ModMultiplayerPresence? tryParse(String value) {
    for (final presence in values) {
      if (presence.wireName == value) return presence;
    }
    return null;
  }
}

class ModMultiplayerProtocol {
  const ModMultiplayerProtocol({
    required this.version,
    this.peerVersionRange,
    this.peerVersionRangeIsPresent = false,
  });

  final String version;

  /// A range negotiated independently from the package version. When absent,
  /// peers must use the exact same [version].
  final String? peerVersionRange;
  final bool peerVersionRangeIsPresent;

  factory ModMultiplayerProtocol.fromJson(Map<String, Object?> json) {
    final range = json['peerVersionRange'];
    return ModMultiplayerProtocol(
      version: json['version'] is String ? json['version']! as String : '',
      peerVersionRange: range is String ? range : null,
      peerVersionRangeIsPresent: json.containsKey('peerVersionRange'),
    );
  }

  Map<String, Object?> toJson() => {
    'version': version,
    if (peerVersionRangeIsPresent && peerVersionRange != null)
      'peerVersionRange': peerVersionRange,
  };
}

class ModMultiplayerMetadata {
  const ModMultiplayerMetadata({
    required this.modeName,
    this.presenceName,
    this.presenceIsPresent = false,
    this.protocol,
    this.protocolIsPresent = false,
    this.synchronizedFiles = const [],
    this.synchronizedFilesIsPresent = false,
  });

  factory ModMultiplayerMetadata.session({
    ModMultiplayerPresence presence = ModMultiplayerPresence.required,
    String protocolVersion = '1.0.0',
    String? peerVersionRange,
    List<String> synchronizedFiles = const [],
  }) => ModMultiplayerMetadata(
    modeName: ModMultiplayerMode.session.wireName,
    presenceName: presence.wireName,
    presenceIsPresent: true,
    protocol: ModMultiplayerProtocol(
      version: protocolVersion,
      peerVersionRange: peerVersionRange,
      peerVersionRangeIsPresent: peerVersionRange != null,
    ),
    protocolIsPresent: true,
    synchronizedFiles: synchronizedFiles,
    synchronizedFilesIsPresent: synchronizedFiles.isNotEmpty,
  );

  final String modeName;
  final String? presenceName;
  final bool presenceIsPresent;
  final ModMultiplayerProtocol? protocol;
  final bool protocolIsPresent;
  final List<String> synchronizedFiles;
  final bool synchronizedFilesIsPresent;

  ModMultiplayerMode? get mode => ModMultiplayerMode.tryParse(modeName);

  ModMultiplayerPresence? get presence => presenceName == null
      ? null
      : ModMultiplayerPresence.tryParse(presenceName!);

  static ModMultiplayerMetadata? tryFromJson(Object? value) {
    if (value is! Map) return null;
    final json = value.map((key, item) => MapEntry(key.toString(), item));
    final protocolValue = json['protocol'];
    return ModMultiplayerMetadata(
      modeName: json['mode'] is String ? json['mode']! as String : '',
      presenceName: json['presence'] is String
          ? json['presence']! as String
          : null,
      presenceIsPresent: json.containsKey('presence'),
      protocol: protocolValue is Map
          ? ModMultiplayerProtocol.fromJson(
              protocolValue.map((key, item) => MapEntry(key.toString(), item)),
            )
          : null,
      protocolIsPresent: json.containsKey('protocol'),
      synchronizedFiles: _stringList(json['synchronizedFiles']),
      synchronizedFilesIsPresent: json.containsKey('synchronizedFiles'),
    );
  }

  Map<String, Object?> toJson() => {
    'mode': modeName,
    if (presenceIsPresent && presenceName != null) 'presence': presenceName,
    if (protocolIsPresent && protocol != null) 'protocol': protocol!.toJson(),
    if (synchronizedFilesIsPresent) 'synchronizedFiles': synchronizedFiles,
  };
}
