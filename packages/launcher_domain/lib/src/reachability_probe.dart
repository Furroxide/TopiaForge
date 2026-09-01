/// Reachability classification for the launcher's opt-in NAT probe.
///
/// The probe exists to answer one question the multiplayer cost model depends on and nobody has measured: what
/// fraction of real players can host a session that other players reach directly? Relay demand in a player-hosted
/// topology is `P(host unreachable) x lobby size` — a property of the host, not of each connection — so `P` is the
/// dominant term. See `docs/internal/MultiplayerTransportOptions.md` and
/// `docs/internal/LauncherReachabilityProbe.md`.
///
/// Nothing here touches Robotopia, and nothing here depends on the multiplayer hosting feasibility gate being
/// closed. It measures the launcher's own host, which is the same host a session would run on.
///
/// **Privacy is structural, not procedural.** [NatObservation] carries only booleans. An address is compared where
/// it is observed, in the data layer, and only the comparison result crosses into the domain. No type in this file
/// can hold an IP address, so no amount of downstream carelessness can log, persist, or report one.
library;

/// RFC 4787 mapping behaviour: does the NAT reuse one external mapping across different destinations?
///
/// Address- or port-dependent mapping is "symmetric" NAT, and is the case a relay cannot be avoided for.
enum NatMappingBehavior {
  /// Not determined — too few transactions completed.
  unknown,

  /// One external mapping is reused for every destination. Hole punching works.
  endpointIndependent,

  /// A new mapping per destination address.
  addressDependent,

  /// A new mapping per destination address and port. Symmetric NAT.
  addressAndPortDependent,
}

/// RFC 4787 filtering behaviour: who is allowed to send inbound traffic to an existing mapping?
enum NatFilteringBehavior {
  /// Not determined — too few transactions completed.
  unknown,

  /// Any external host may use the mapping. Full-cone.
  endpointIndependent,

  /// Only a host the client has already sent to. Restricted-cone.
  addressDependent,

  /// Only an address and port the client has already sent to. Port-restricted-cone.
  addressAndPortDependent,
}

/// The single derived answer the cost model consumes.
///
/// Ordered from cheapest to most expensive to serve.
enum HostReachability {
  /// The probe did not produce enough evidence to classify.
  unknown,

  /// No NAT in the path: the observed external endpoint is the local endpoint. Reachable without assistance.
  direct,

  /// Behind a NAT that reuses one mapping. A direct path is establishable by hole punching.
  holePunchable,

  /// Behind an address- or port-dependent (symmetric) NAT. A relay is required.
  relayRequired,

  /// No UDP response was observed at all. Either UDP is blocked or the probe could not reach any server.
  udpBlocked;

  /// Whether a lobby that elects this host would need to relay every peer.
  ///
  /// [unknown] counts as needing a relay. An unclassified host is not evidence of a good host, and host election
  /// (`CP-R2`) must not treat missing evidence as a pass.
  bool get requiresRelay =>
      this == HostReachability.relayRequired ||
      this == HostReachability.udpBlocked ||
      this == HostReachability.unknown;
}

/// The PII-free evidence a probe run produces.
///
/// Every field is a boolean comparison already performed against a raw endpoint that has since been discarded. The
/// class deliberately has no field capable of holding an address, port, hostname, or timestamp.
class NatObservation {
  const NatObservation({
    this.respondedAtAll = false,
    this.mappedMatchesLocalEndpoint = false,
    this.sameMappingAcrossServerAddresses = false,
    this.sameMappingAcrossServerPorts = false,
    this.acceptedFromUnsolicitedAddress = false,
    this.acceptedFromUnsolicitedPort = false,
    this.completedMappingTransactions = 0,
  });

  /// Any STUN binding response was received.
  final bool respondedAtAll;

  /// The externally observed endpoint equals the local socket endpoint — no NAT translation happened.
  final bool mappedMatchesLocalEndpoint;

  /// The same external endpoint was observed when probing two different server addresses.
  final bool sameMappingAcrossServerAddresses;

  /// The same external endpoint was observed when probing two ports on one server address.
  final bool sameMappingAcrossServerPorts;

  /// A response arrived from an address the client had not sent to.
  final bool acceptedFromUnsolicitedAddress;

  /// A response arrived from a port the client had not sent to, on an address it had.
  final bool acceptedFromUnsolicitedPort;

  /// How many of the three mapping transactions returned a reflexive address. Below
  /// [ReachabilityClassifier.minimumMappingTransactions] the mapping behaviour is reported as unknown rather than
  /// guessed. Filtering probes are not counted here: a filtering probe that gets no answer is itself the result.
  final int completedMappingTransactions;
}

/// A classified probe result. Carries no identifying information by construction.
class NatClassification {
  const NatClassification({
    required this.reachability,
    required this.mapping,
    required this.filtering,
  });

  /// An unclassified result, used before a probe has run and whenever evidence is insufficient.
  static const unknown = NatClassification(
    reachability: HostReachability.unknown,
    mapping: NatMappingBehavior.unknown,
    filtering: NatFilteringBehavior.unknown,
  );

  final HostReachability reachability;
  final NatMappingBehavior mapping;
  final NatFilteringBehavior filtering;

  @override
  bool operator ==(Object other) =>
      other is NatClassification &&
      other.reachability == reachability &&
      other.mapping == mapping &&
      other.filtering == filtering;

  @override
  int get hashCode => Object.hash(reachability, mapping, filtering);

  @override
  String toString() =>
      'NatClassification(${reachability.name}, mapping: ${mapping.name}, filtering: ${filtering.name})';
}

/// Derives a [NatClassification] from PII-free probe evidence.
///
/// Pure and total: every [NatObservation] maps to some classification, and insufficient evidence produces
/// [HostReachability.unknown] rather than an optimistic guess.
class ReachabilityClassifier {
  const ReachabilityClassifier();

  /// Mapping behaviour needs at least a same-address/different-port and a different-address transaction to
  /// distinguish address-dependent from address-and-port-dependent.
  static const minimumMappingTransactions = 3;

  NatClassification classify(NatObservation observation) {
    if (!observation.respondedAtAll) {
      return const NatClassification(
        reachability: HostReachability.udpBlocked,
        mapping: NatMappingBehavior.unknown,
        filtering: NatFilteringBehavior.unknown,
      );
    }

    final filtering = _filtering(observation);

    if (observation.mappedMatchesLocalEndpoint) {
      return NatClassification(
        reachability: HostReachability.direct,
        mapping: NatMappingBehavior.endpointIndependent,
        filtering: filtering,
      );
    }

    if (observation.completedMappingTransactions < minimumMappingTransactions) {
      return NatClassification(
        reachability: HostReachability.unknown,
        mapping: NatMappingBehavior.unknown,
        filtering: filtering,
      );
    }

    final mapping = _mapping(observation);
    return NatClassification(
      reachability: mapping == NatMappingBehavior.endpointIndependent
          ? HostReachability.holePunchable
          : HostReachability.relayRequired,
      mapping: mapping,
      filtering: filtering,
    );
  }

  NatMappingBehavior _mapping(NatObservation observation) {
    if (observation.sameMappingAcrossServerAddresses &&
        observation.sameMappingAcrossServerPorts) {
      return NatMappingBehavior.endpointIndependent;
    }
    // A mapping that survives a port change but not an address change keys on the destination address only.
    if (observation.sameMappingAcrossServerPorts) {
      return NatMappingBehavior.addressDependent;
    }
    return NatMappingBehavior.addressAndPortDependent;
  }

  NatFilteringBehavior _filtering(NatObservation observation) {
    if (observation.acceptedFromUnsolicitedAddress) {
      return NatFilteringBehavior.endpointIndependent;
    }
    if (observation.acceptedFromUnsolicitedPort) {
      return NatFilteringBehavior.addressDependent;
    }
    return NatFilteringBehavior.addressAndPortDependent;
  }
}
