part of '../launch_resolution.dart';

/// Cached observations are accepted only through matching package provenance.
final class RuntimeObservation {
  const RuntimeObservation._({
    this.profileId = '',
    this.profileRevision = 0,
    this.digest = '',
    this.discoveredWorlds = const [],
    this.availability = const [],
  });
  static const none = RuntimeObservation._();

  factory RuntimeObservation.fromEnvelopes(
    EffectiveProfile profile,
    Iterable<LaunchObservationEnvelope> envelopes,
  ) {
    final index = _OwnerIndex(profile);
    final digest = _profileDigest(profile);
    final grouped = <String, List<LaunchObservationEnvelope>>{};
    for (final envelope in envelopes) {
      if (envelope.profileId != profile.profileId ||
          envelope.profileRevision != profile.revision ||
          envelope.packageSetDigest != digest) {
        continue;
      }
      final producer = profile.packages
          .where(
            (item) =>
                item.id == envelope.producer.id &&
                item.version == envelope.producer.version,
          )
          .toList();
      if (producer.length != 1 || index.ambiguities.isNotEmpty) continue;
      grouped.putIfAbsent(envelope.producer.id, () => []).add(envelope);
    }
    final worlds = <DiscoveredWorldObservation>[];
    final availability = <LaunchAvailability>[];
    for (final group in grouped.values) {
      group.sort(
        (a, b) => b.observationRevision.compareTo(a.observationRevision),
      );
      final newest = group
          .where(
            (item) =>
                item.observationRevision == group.first.observationRevision,
          )
          .toList();
      if (newest.map((item) => jsonEncode(item.toJson())).toSet().length != 1) {
        continue;
      }
      final envelope = newest.first;
      final producer = profile.packages.firstWhere(
        (item) => item.id == envelope.producer.id,
      );
      final declared = index.manifest(producer).contributions;
      final accepted = <DiscoveredWorldObservation>[];
      for (final instance in envelope.discoveredWorlds) {
        if (!_observationOwner(index, instance.id, producer) ||
            !_observationOwner(index, instance.familyId, producer)) {
          continue;
        }
        final families =
            (declared?.worlds ?? const <ModWorldDeclaration>[])
                .where(
                  (item) =>
                      _isDiscovered(item) &&
                      instance.id.toLowerCase().startsWith(
                        '${item.id.toLowerCase()}.',
                      ),
                )
                .toList()
              ..sort((a, b) => b.id.length.compareTo(a.id.length));
        if (families.isEmpty ||
            !_idEquals(families.first.id, instance.familyId) ||
            families
                    .where((item) => item.id.length == families.first.id.length)
                    .length !=
                1) {
          continue;
        }
        accepted.add(instance);
      }
      worlds.addAll(accepted);
      for (final record in envelope.availability) {
        if (!_observationOwner(index, record.id, producer)) continue;
        final exists = record.kind == 'gamemode'
            ? (declared?.gamemodes ?? const <ModGamemodeDeclaration>[]).any(
                (item) => _idEquals(item.id, record.id),
              )
            : (declared?.worlds ?? const <ModWorldDeclaration>[]).any(
                    (item) => _idEquals(item.id, record.id),
                  ) ||
                  accepted.any((item) => _idEquals(item.id, record.id));
        if (exists) availability.add(record);
      }
    }
    worlds.sort((a, b) => a.id.compareTo(b.id));
    availability.sort(
      (a, b) => '${a.kind}:${a.id}'.compareTo('${b.kind}:${b.id}'),
    );
    return RuntimeObservation._(
      profileId: profile.profileId,
      profileRevision: profile.revision,
      digest: digest,
      discoveredWorlds: List.unmodifiable(worlds),
      availability: List.unmodifiable(availability),
    );
  }
  final String profileId;
  final int profileRevision;
  final String digest;
  final List<DiscoveredWorldObservation> discoveredWorlds;
  final List<LaunchAvailability> availability;
  List<String> get unavailableWorldIds =>
      _withCode('world', LaunchBlockCode.worldUnavailable);
  List<String> get unboundWorldIds =>
      _withCode('world', LaunchBlockCode.worldUnbound);
  List<String> get unboundGamemodeIds =>
      _withCode('gamemode', LaunchBlockCode.gamemodeUnbound);
  List<String> _withCode(String kind, LaunchBlockCode code) => availability
      .where(
        (item) =>
            item.kind == kind && item.blocks.any((block) => block.code == code),
      )
      .map((item) => item.id)
      .toList();
  bool matches(EffectiveProfile profile) =>
      identical(this, none) ||
      (profileId == profile.profileId &&
          profileRevision == profile.revision &&
          digest == _profileDigest(profile));
}

bool _observationOwner(_OwnerIndex index, String id, ResolvedPackage producer) {
  final owner = index.owner(id, []);
  return owner?.ambiguous == false && identical(owner?.enabled, producer);
}
