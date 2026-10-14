part of '../launch_resolution.dart';

/// One preference the player changed in game, numbered by [sequence].
sealed class MultiplayerPreferenceChange {
  MultiplayerPreferenceChange._(int sequence)
    : sequence = _ranged(sequence, 1, 2147483647);

  factory MultiplayerPreferenceChange.fromJson(Object? value) {
    if (value is! Map<String, Object?>) {
      throw const FormatException('Invalid preference change.');
    }
    final changed = value.keys.where((key) => key != 'sequence').toList();
    if (changed.length != 1 || !value.containsKey('sequence')) {
      throw const FormatException('A change sets exactly one preference.');
    }
    final sequence = _ranged(value['sequence'], 1, 2147483647);
    final change = value[changed.single];
    switch (changed.single) {
      case 'displayName':
        return DisplayNameChange(
          sequence: sequence,
          displayName: _text(change, max: 64),
        );
      case 'disclosure':
        return DisclosureChange(
          sequence: sequence,
          disclosure: _integer(change),
        );
      case 'voice':
        final json = _object(
          change,
          {'consent', 'enabled'},
          {'consent', 'enabled'},
        );
        return VoiceChange(
          sequence: sequence,
          consent: _integer(json['consent']),
          enabled: _boolean(json['enabled']),
        );
      case 'network':
        final json = _object(change, {'lan', 'upnp'}, {'lan', 'upnp'});
        return NetworkChange(
          sequence: sequence,
          lanDiscovery: _boolean(json['lan']),
          upnp: _boolean(json['upnp']),
        );
      case 'hosting':
        return HostingChange(
          sequence: sequence,
          hosting: MultiplayerHostingDefaults.fromJson(change),
        );
      default:
        throw const FormatException('Unknown preference change.');
    }
  }

  final int sequence;

  Map<String, Object?> toJson();
}

final class DisplayNameChange extends MultiplayerPreferenceChange {
  DisplayNameChange({required int sequence, required String displayName})
    : displayName = _displayText(
        displayName,
        MultiplayerSettings.maximumDisplayNameLength,
        minimum: MultiplayerSettings.minimumDisplayNameLength,
      ),
      super._(sequence);

  final String displayName;

  @override
  Map<String, Object?> toJson() => {
    'displayName': displayName,
    'sequence': sequence,
  };
}

/// The player acknowledged the address disclosure in game. An
/// acknowledgement never lowers the version the launcher already holds.
final class DisclosureChange extends MultiplayerPreferenceChange {
  DisclosureChange({required int sequence, required int disclosure})
    : disclosure = _ranged(disclosure, 1, 2147483647),
      super._(sequence);

  final int disclosure;

  @override
  Map<String, Object?> toJson() => {
    'disclosure': disclosure,
    'sequence': sequence,
  };
}

/// The player gave or withdrew microphone consent, or switched voice.
final class VoiceChange extends MultiplayerPreferenceChange {
  VoiceChange({
    required int sequence,
    required int consent,
    required this.enabled,
  }) : consent = _integer(consent),
       super._(sequence) {
    if (enabled && this.consent < 1) {
      throw const FormatException('Voice stays off until the player consents.');
    }
  }

  final int consent;
  final bool enabled;

  @override
  Map<String, Object?> toJson() => {
    'sequence': sequence,
    'voice': {'consent': consent, 'enabled': enabled},
  };
}

final class NetworkChange extends MultiplayerPreferenceChange {
  NetworkChange({
    required int sequence,
    required this.lanDiscovery,
    required this.upnp,
  }) : super._(sequence);

  final bool lanDiscovery;
  final bool upnp;

  @override
  Map<String, Object?> toJson() => {
    'network': {'lan': lanDiscovery, 'upnp': upnp},
    'sequence': sequence,
  };
}

final class HostingChange extends MultiplayerPreferenceChange {
  HostingChange({required int sequence, required this.hosting})
    : super._(sequence);

  final MultiplayerHostingDefaults hosting;

  @override
  Map<String, Object?> toJson() => {
    'hosting': hosting.toJson(),
    'sequence': sequence,
  };
}

/// Preferences the player changed in game
/// (`multiplayer-preferences-journal-v1.json` in manager staging, game to
/// launcher): at most 64 changes in strictly increasing [sequence] order.
/// The game appends and rewrites the whole file atomically, and drops the
/// changes a projection's `journalApplied` covers.
final class MultiplayerPreferencesJournal {
  MultiplayerPreferencesJournal(Iterable<MultiplayerPreferenceChange> changes)
    : changes = List.unmodifiable(changes.take(maximumChanges + 1)) {
    if (this.changes.length > maximumChanges) {
      throw const FormatException('The journal holds at most 64 changes.');
    }
    for (var index = 1; index < this.changes.length; index++) {
      if (this.changes[index].sequence <= this.changes[index - 1].sequence) {
        throw const FormatException('Journal sequences increase strictly.');
      }
    }
  }

  static const schemaVersion = 1;
  static const maximumChanges = 64;

  factory MultiplayerPreferencesJournal.fromJson(Object? value) {
    final json = _object(
      value,
      {'changes', 'schemaVersion'},
      {'changes', 'schemaVersion'},
    );
    if (_integer(json['schemaVersion']) != schemaVersion) {
      throw const FormatException('Unsupported preferences journal.');
    }
    return MultiplayerPreferencesJournal(
      _list(
        json['changes'],
        MultiplayerPreferenceChange.fromJson,
        max: maximumChanges,
      ),
    );
  }

  final List<MultiplayerPreferenceChange> changes;

  /// [projection] with every change after its `journalApplied` applied in
  /// order, as the next revision; [projection] itself when nothing is new.
  MultiplayerPreferencesProjection applyTo(
    MultiplayerPreferencesProjection projection,
  ) {
    final fresh = changes
        .where((change) => change.sequence > projection.journalApplied)
        .toList();
    if (fresh.isEmpty) return projection;
    var next = projection;
    for (final change in fresh) {
      final revision = projection.revision + 1;
      next = switch (change) {
        DisplayNameChange(:final displayName) => next._with(
          revision: revision,
          journalApplied: change.sequence,
          displayName: displayName,
        ),
        DisclosureChange(:final disclosure) => next._with(
          revision: revision,
          journalApplied: change.sequence,
          disclosure: disclosure > next.disclosure
              ? disclosure
              : next.disclosure,
        ),
        VoiceChange(:final consent, :final enabled) => next._with(
          revision: revision,
          journalApplied: change.sequence,
          voiceConsent: consent,
          voiceEnabled: enabled,
        ),
        NetworkChange(:final lanDiscovery, :final upnp) => next._with(
          revision: revision,
          journalApplied: change.sequence,
          lanDiscovery: lanDiscovery,
          upnp: upnp,
        ),
        HostingChange(:final hosting) => next._with(
          revision: revision,
          journalApplied: change.sequence,
          hosting: hosting,
        ),
      };
    }
    return next;
  }

  Map<String, Object?> toJson() =>
      _sorted({
            'changes': [for (final change in changes) change.toJson()],
            'schemaVersion': schemaVersion,
          })!
          as Map<String, Object?>;
}
