/// Opt-in settings, the aggregate report shape, and the gates that keep the reachability probe from becoming
/// telemetry.
///
/// `docs/PrivacyAndCapabilities.md` makes an approved privacy notice a release blocker for every TopiaForge data
/// collection. The probe therefore ships **off**, behind developer mode, and with reporting held shut by a constant
/// that engineering may not flip — see [ReachabilityProbePolicy.reportingApproved].
library;

import 'reachability_probe.dart';

/// Player-controlled probe settings. Persisted next to the launcher's other local state.
///
/// Both flags default to `false`. A missing or malformed settings file must therefore decode to "off", never to a
/// default-on state; [ReachabilityProbeSettings.fromJson] treats any non-`true` value as `false`.
class ReachabilityProbeSettings {
  const ReachabilityProbeSettings({
    this.enabled = false,
    this.shareAggregateResults = false,
  });

  /// Whether the probe may run at all. Default off.
  final bool enabled;

  /// Whether the player has agreed that an aggregate, address-free result may be shared.
  ///
  /// This is the player's half of the consent. It is necessary but **not sufficient**: reporting also requires
  /// [ReachabilityProbePolicy.reportingApproved], which is the project's half and is currently `false`.
  final bool shareAggregateResults;

  factory ReachabilityProbeSettings.fromJson(Map<String, Object?> json) {
    return ReachabilityProbeSettings(
      enabled: json['enabled'] == true,
      shareAggregateResults: json['shareAggregateResults'] == true,
    );
  }

  Map<String, Object?> toJson() => {
    'enabled': enabled,
    'shareAggregateResults': shareAggregateResults,
  };

  ReachabilityProbeSettings copyWith({
    bool? enabled,
    bool? shareAggregateResults,
  }) {
    return ReachabilityProbeSettings(
      enabled: enabled ?? this.enabled,
      shareAggregateResults:
          shareAggregateResults ?? this.shareAggregateResults,
    );
  }

  @override
  bool operator ==(Object other) =>
      other is ReachabilityProbeSettings &&
      other.enabled == enabled &&
      other.shareAggregateResults == shareAggregateResults;

  @override
  int get hashCode => Object.hash(enabled, shareAggregateResults);
}

/// Why a probe run or a report was refused. Surfaced to the developer pane so a refusal is legible rather than
/// silent.
enum ReachabilityProbeRefusal {
  /// Developer mode is off. The probe is not a player-facing feature.
  developerModeRequired,

  /// The probe itself has not been switched on.
  notEnabled,

  /// The player has not agreed to share an aggregate result.
  sharingNotConsented,

  /// No approved privacy notice covers this collection. See [ReachabilityProbePolicy.reportingApproved].
  privacyNoticeNotApproved,
}

/// The decisions that stand between a probe existing and a probe collecting data.
///
/// Kept as pure functions with an explicit refusal reason so the tests can assert the negative cases directly —
/// "off by default" is the property most worth testing and the easiest to regress.
class ReachabilityProbePolicy {
  const ReachabilityProbePolicy();

  /// Whether a privacy notice covering reachability collection has been approved.
  ///
  /// **This is `false` and must stay `false` until the privacy/legal owner supplies approved text.**
  /// `docs/PrivacyAndCapabilities.md` lists an approved privacy notice as a release blocker; this constant is that
  /// blocker expressed in code, so no wiring mistake can turn the probe into shipped telemetry. Flipping it is a
  /// deliberate, reviewable change, not a configuration value.
  static const bool reportingApproved = false;

  /// Whether the probe may run locally, showing its result only to the person who ran it.
  ///
  /// Running locally is not collection: nothing leaves the machine, so this does not require the privacy notice.
  ReachabilityProbeRefusal? refuseRun({
    required bool developerMode,
    required ReachabilityProbeSettings settings,
  }) {
    if (!developerMode) return ReachabilityProbeRefusal.developerModeRequired;
    if (!settings.enabled) return ReachabilityProbeRefusal.notEnabled;
    return null;
  }

  /// Whether an aggregate result may leave the machine.
  ///
  /// Requires everything [refuseRun] requires, plus explicit sharing consent, plus an approved privacy notice.
  ReachabilityProbeRefusal? refuseReport({
    required bool developerMode,
    required ReachabilityProbeSettings settings,
  }) {
    final runRefusal = refuseRun(
      developerMode: developerMode,
      settings: settings,
    );
    if (runRefusal != null) return runRefusal;
    if (!settings.shareAggregateResults) {
      return ReachabilityProbeRefusal.sharingNotConsented;
    }
    if (!reportingApproved) {
      return ReachabilityProbeRefusal.privacyNoticeNotApproved;
    }
    return null;
  }
}

/// The complete payload that would ever be reported for one probe run.
///
/// Aggregate by construction: three enum names and a schema version. There is no field for an address, hostname,
/// ISP, region, machine identifier, session identifier, or timestamp. A population distribution is recoverable by
/// counting these; an individual is not identifiable from one.
class ReachabilityReport {
  const ReachabilityReport({required this.classification});

  /// Bumped only when the field set changes, so a receiver can reject a shape it does not understand.
  static const schemaVersion = 1;

  final NatClassification classification;

  Map<String, Object?> toJson() => {
    'schemaVersion': schemaVersion,
    'reachability': classification.reachability.name,
    'mapping': classification.mapping.name,
    'filtering': classification.filtering.name,
  };
}

/// The result of asking for a probe run.
class ReachabilityProbeOutcome {
  const ReachabilityProbeOutcome._({
    required this.classification,
    this.refusal,
    this.message,
  });

  /// A run that completed and produced a classification.
  const ReachabilityProbeOutcome.completed(NatClassification classification)
    : this._(classification: classification);

  /// A run a policy gate declined, with the reason.
  const ReachabilityProbeOutcome.refused(ReachabilityProbeRefusal refusal)
    : this._(classification: NatClassification.unknown, refusal: refusal);

  /// A run that was permitted but could not proceed, with an operator-facing explanation.
  const ReachabilityProbeOutcome.unavailable(String message)
    : this._(classification: NatClassification.unknown, message: message);

  final NatClassification classification;
  final ReachabilityProbeRefusal? refusal;
  final String? message;

  /// Whether a classification was actually produced.
  bool get ran => refusal == null && message == null;
}

/// What the launcher UI is allowed to ask of the probe.
///
/// Deliberately narrow: read the opt-in, write the opt-in, and run once locally. There is **no** report method,
/// because nothing may send a result while [ReachabilityProbePolicy.reportingApproved] is `false`. Adding one is a
/// reviewable change, not a wiring detail.
abstract class ReachabilityProbeGateway {
  Future<ReachabilityProbeSettings> loadSettings();

  Future<void> saveSettings(ReachabilityProbeSettings settings);

  Future<ReachabilityProbeOutcome> run({required bool developerMode});
}
