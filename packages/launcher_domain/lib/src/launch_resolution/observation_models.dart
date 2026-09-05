part of '../launch_resolution.dart';

final class DiscoveredWorldObservation {
  DiscoveredWorldObservation({
    required String id,
    required String familyId,
    required String name,
    String? description,
  }) : id = _declaration(id),
       familyId = _declaration(familyId),
       name = _text(name, max: 128),
       description = description == null
           ? null
           : _text(description, max: 1024, empty: true) {
    if (!this.id.toLowerCase().startsWith('${this.familyId.toLowerCase()}.') ||
        this.id.length <= this.familyId.length + 1) {
      throw const FormatException(
        'Discovery id must have a nonempty suffix beneath its family.',
      );
    }
  }
  factory DiscoveredWorldObservation.fromJson(Object? value) {
    final json = _object(
      value,
      {'id', 'familyId', 'name', 'description'},
      {'id', 'familyId', 'name'},
    );
    return DiscoveredWorldObservation(
      id: _declaration(json['id']),
      familyId: _declaration(json['familyId']),
      name: _text(json['name'], max: 128),
      description: json.containsKey('description')
          ? _text(json['description'], max: 1024, empty: true)
          : null,
    );
  }
  final String id;
  final String familyId;
  final String name;
  final String? description;
  Map<String, Object?> toJson() => {
    'id': id,
    'familyId': familyId,
    'name': name,
    if (description != null) 'description': description,
  };
}

final class LaunchAvailability {
  LaunchAvailability({
    required String kind,
    required String id,
    required Iterable<LaunchBlock> blocks,
  }) : kind = _choice(kind, const ['world', 'gamemode']),
       id = _declaration(id),
       blocks = _orderedBlocks(_boundedCopy(blocks)) {
    if (this.blocks.isEmpty) {
      throw const FormatException(
        'Availability records contain failures only.',
      );
    }
  }
  factory LaunchAvailability.fromJson(Object? value) {
    final json = _object(
      value,
      {'kind', 'id', 'blocks'},
      {'kind', 'id', 'blocks'},
    );
    return LaunchAvailability(
      kind: _text(json['kind']),
      id: _declaration(json['id']),
      blocks: _list(json['blocks'], LaunchBlock.fromJson),
    );
  }
  final String kind;
  final String id;
  final List<LaunchBlock> blocks;
  Map<String, Object?> toJson() => {
    'kind': kind,
    'id': id,
    'blocks': blocks.map((item) => item.toJson()).toList(),
  };
}

final class LaunchObservationEnvelope {
  LaunchObservationEnvelope({
    required String profileId,
    required int profileRevision,
    required PackageIdentity producer,
    required String packageSetDigest,
    required int observationRevision,
    Iterable<DiscoveredWorldObservation> discoveredWorlds = const [],
    Iterable<LaunchAvailability> availability = const [],
  }) : profileId = _token(profileId),
       profileRevision = _integer(profileRevision),
       producer = PackageIdentity(id: producer.id, version: producer.version),
       packageSetDigest = _digest(packageSetDigest),
       observationRevision = _integer(observationRevision),
       discoveredWorlds = _boundedCopy(discoveredWorlds),
       availability = _boundedCopy(availability) {
    final seen = <String>{};
    if (this.discoveredWorlds.length > 4096 ||
        this.availability.length > 4096 ||
        this.discoveredWorlds.any((item) => !seen.add(item.id.toLowerCase()))) {
      throw const FormatException('Duplicate or excessive discoveries.');
    }
    seen.clear();
    if (this.availability.any(
      (item) => !seen.add('${item.kind}:${item.id.toLowerCase()}'),
    )) {
      throw const FormatException('Duplicate availability records.');
    }
  }
  factory LaunchObservationEnvelope.fromJson(Object? value) {
    final json = _object(
      value,
      {
        'schemaVersion',
        'profileId',
        'profileRevision',
        'producer',
        'packageSetDigest',
        'observationRevision',
        'discoveredWorlds',
        'availability',
      },
      {
        'schemaVersion',
        'profileId',
        'profileRevision',
        'producer',
        'packageSetDigest',
        'observationRevision',
        'discoveredWorlds',
        'availability',
      },
    );
    if (_integer(json['schemaVersion']) != 1) {
      throw const FormatException('Unsupported observation version.');
    }
    return LaunchObservationEnvelope(
      profileId: _token(json['profileId']),
      profileRevision: _integer(json['profileRevision']),
      producer: PackageIdentity.fromJson(json['producer']),
      packageSetDigest: _digest(json['packageSetDigest']),
      observationRevision: _integer(json['observationRevision']),
      discoveredWorlds: _list(
        json['discoveredWorlds'],
        DiscoveredWorldObservation.fromJson,
      ),
      availability: _list(json['availability'], LaunchAvailability.fromJson),
    );
  }
  final String profileId;
  final int profileRevision;
  final PackageIdentity producer;
  final String packageSetDigest;
  final int observationRevision;
  final List<DiscoveredWorldObservation> discoveredWorlds;
  final List<LaunchAvailability> availability;
  Map<String, Object?> toJson() {
    final worlds = [...discoveredWorlds]..sort((a, b) => a.id.compareTo(b.id));
    final reasons = [...availability]
      ..sort((a, b) {
        final kind = a.kind.compareTo(b.kind);
        return kind != 0 ? kind : a.id.compareTo(b.id);
      });
    return {
      'schemaVersion': 1,
      'profileId': profileId,
      'profileRevision': profileRevision,
      'producer': producer.toJson(),
      'packageSetDigest': packageSetDigest,
      'observationRevision': observationRevision,
      'discoveredWorlds': worlds.map((item) => item.toJson()).toList(),
      'availability': reasons.map((item) => item.toJson()).toList(),
    };
  }
}

/// Fresh runtime evidence is supplied explicitly; cached success never binds code.
final class RuntimeBindingSnapshot {
  RuntimeBindingSnapshot({
    required String profileId,
    required int profileRevision,
    required String packageSetDigest,
    Iterable<String> boundWorldIds = const [],
    Iterable<String> boundGamemodeIds = const [],
    Iterable<LaunchAvailability> availability = const [],
  }) : profileId = _token(profileId),
       profileRevision = _integer(profileRevision),
       packageSetDigest = _digest(packageSetDigest),
       boundWorldIds = _ids(boundWorldIds.toList()),
       boundGamemodeIds = _ids(boundGamemodeIds.toList()),
       availability = _boundedCopy(availability) {
    final records = <String>{};
    if (this.availability.length > 4096 ||
        this.availability.any(
          (item) =>
              !records.add('${item.kind}:${item.id.toLowerCase()}') ||
              (item.kind == 'world'
                      ? this.boundWorldIds
                      : this.boundGamemodeIds)
                  .any((id) => _idEquals(id, item.id)),
        )) {
      throw const FormatException('Conflicting runtime binding evidence.');
    }
  }
  RuntimeBindingSnapshot._missing(EffectiveProfile profile)
    : profileId = profile.profileId,
      profileRevision = profile.revision,
      packageSetDigest = _profileDigest(profile),
      boundWorldIds = const [],
      boundGamemodeIds = const [],
      availability = const [];
  final String profileId;
  final int profileRevision;
  final String packageSetDigest;
  final List<String> boundWorldIds;
  final List<String> boundGamemodeIds;
  final List<LaunchAvailability> availability;
  bool matches(EffectiveProfile profile) =>
      profileId == profile.profileId &&
      profileRevision == profile.revision &&
      packageSetDigest == _profileDigest(profile);
}

String _profileDigest(EffectiveProfile profile) =>
    packageSetDigest(profile.packages);
