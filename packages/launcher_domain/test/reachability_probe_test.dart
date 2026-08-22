import 'package:launcher_domain/launcher_domain.dart';
import 'package:test/test.dart';

void main() {
  group('ReachabilityClassifier', () {
    const classifier = ReachabilityClassifier();

    test('reports UDP blocked when nothing responds', () {
      final result = classifier.classify(const NatObservation());

      expect(result.reachability, HostReachability.udpBlocked);
      expect(result.mapping, NatMappingBehavior.unknown);
      expect(result.filtering, NatFilteringBehavior.unknown);
    });

    test('reports a direct host when the mapping is the local endpoint', () {
      final result = classifier.classify(
        const NatObservation(
          respondedAtAll: true,
          mappedMatchesLocalEndpoint: true,
          acceptedFromUnsolicitedAddress: true,
          completedMappingTransactions: 4,
        ),
      );

      expect(result.reachability, HostReachability.direct);
      expect(result.mapping, NatMappingBehavior.endpointIndependent);
      expect(result.filtering, NatFilteringBehavior.endpointIndependent);
    });

    test('reports hole-punchable behind an endpoint-independent NAT', () {
      final result = classifier.classify(
        const NatObservation(
          respondedAtAll: true,
          sameMappingAcrossServerAddresses: true,
          sameMappingAcrossServerPorts: true,
          completedMappingTransactions: 4,
        ),
      );

      expect(result.reachability, HostReachability.holePunchable);
      expect(result.mapping, NatMappingBehavior.endpointIndependent);
      // No unsolicited traffic accepted: port-restricted, still punchable.
      expect(result.filtering, NatFilteringBehavior.addressAndPortDependent);
    });

    test('reports relay required behind a symmetric NAT', () {
      final result = classifier.classify(
        const NatObservation(
          respondedAtAll: true,
          completedMappingTransactions: 4,
        ),
      );

      expect(result.reachability, HostReachability.relayRequired);
      expect(result.mapping, NatMappingBehavior.addressAndPortDependent);
    });

    test('reports address-dependent mapping as relay required', () {
      final result = classifier.classify(
        const NatObservation(
          respondedAtAll: true,
          sameMappingAcrossServerPorts: true,
          completedMappingTransactions: 4,
        ),
      );

      expect(result.mapping, NatMappingBehavior.addressDependent);
      expect(result.reachability, HostReachability.relayRequired);
    });

    test('refuses to guess mapping behaviour from too few transactions', () {
      final result = classifier.classify(
        const NatObservation(
          respondedAtAll: true,
          sameMappingAcrossServerAddresses: true,
          sameMappingAcrossServerPorts: true,
          completedMappingTransactions: 2,
        ),
      );

      expect(result.reachability, HostReachability.unknown);
      expect(result.mapping, NatMappingBehavior.unknown);
    });

    test('derives filtering behaviour independently of mapping', () {
      final restricted = classifier.classify(
        const NatObservation(
          respondedAtAll: true,
          sameMappingAcrossServerAddresses: true,
          sameMappingAcrossServerPorts: true,
          acceptedFromUnsolicitedPort: true,
          completedMappingTransactions: 4,
        ),
      );

      expect(restricted.filtering, NatFilteringBehavior.addressDependent);
      expect(restricted.mapping, NatMappingBehavior.endpointIndependent);
    });
  });

  group('HostReachability.requiresRelay', () {
    test('treats missing evidence as needing a relay', () {
      // Host election must not read "we could not classify" as "good host".
      expect(HostReachability.unknown.requiresRelay, isTrue);
      expect(HostReachability.udpBlocked.requiresRelay, isTrue);
      expect(HostReachability.relayRequired.requiresRelay, isTrue);
    });

    test('only a usable direct path avoids a relay', () {
      expect(HostReachability.direct.requiresRelay, isFalse);
      expect(HostReachability.holePunchable.requiresRelay, isFalse);
    });
  });

  group('ReachabilityProbeSettings', () {
    test('defaults to off', () {
      const settings = ReachabilityProbeSettings();

      expect(settings.enabled, isFalse);
      expect(settings.shareAggregateResults, isFalse);
    });

    test('decodes an empty or malformed document to off', () {
      expect(ReachabilityProbeSettings.fromJson(const {}).enabled, isFalse);
      expect(
        ReachabilityProbeSettings.fromJson(const {
          'enabled': 'yes',
          'shareAggregateResults': 1,
        }),
        const ReachabilityProbeSettings(),
      );
    });

    test('round-trips through JSON', () {
      const settings = ReachabilityProbeSettings(
        enabled: true,
        shareAggregateResults: true,
      );

      expect(ReachabilityProbeSettings.fromJson(settings.toJson()), settings);
    });
  });

  group('ReachabilityProbePolicy', () {
    const policy = ReachabilityProbePolicy();

    test('refuses to run outside developer mode', () {
      expect(
        policy.refuseRun(
          developerMode: false,
          settings: const ReachabilityProbeSettings(enabled: true),
        ),
        ReachabilityProbeRefusal.developerModeRequired,
      );
    });

    test('refuses to run until explicitly enabled', () {
      expect(
        policy.refuseRun(
          developerMode: true,
          settings: const ReachabilityProbeSettings(),
        ),
        ReachabilityProbeRefusal.notEnabled,
      );
    });

    test('allows a local run once opted in under developer mode', () {
      expect(
        policy.refuseRun(
          developerMode: true,
          settings: const ReachabilityProbeSettings(enabled: true),
        ),
        isNull,
      );
    });

    test('refuses to report without sharing consent', () {
      expect(
        policy.refuseReport(
          developerMode: true,
          settings: const ReachabilityProbeSettings(enabled: true),
        ),
        ReachabilityProbeRefusal.sharingNotConsented,
      );
    });

    test('refuses to report while no privacy notice is approved', () {
      // The player consenting is necessary but not sufficient. This is the release blocker in
      // docs/PrivacyAndCapabilities.md expressed as a test: the probe cannot become telemetry by accident.
      expect(
        policy.refuseReport(
          developerMode: true,
          settings: const ReachabilityProbeSettings(
            enabled: true,
            shareAggregateResults: true,
          ),
        ),
        ReachabilityProbeRefusal.privacyNoticeNotApproved,
      );
    });

    test('reporting approval is off in this build', () {
      expect(ReachabilityProbePolicy.reportingApproved, isFalse);
    });
  });

  group('ReachabilityReport', () {
    test('carries only a schema version and three classifications', () {
      const report = ReachabilityReport(
        classification: NatClassification(
          reachability: HostReachability.relayRequired,
          mapping: NatMappingBehavior.addressAndPortDependent,
          filtering: NatFilteringBehavior.addressDependent,
        ),
      );

      expect(report.toJson(), {
        'schemaVersion': 1,
        'reachability': 'relayRequired',
        'mapping': 'addressAndPortDependent',
        'filtering': 'addressDependent',
      });
    });

    test('has no field that can carry an address', () {
      const report = ReachabilityReport(
        classification: NatClassification.unknown,
      );

      // Guards the privacy claim structurally: if someone adds an endpoint field, this fails.
      expect(report.toJson().keys.toSet(), {
        'schemaVersion',
        'reachability',
        'mapping',
        'filtering',
      });
      expect(
        report.toJson().values.whereType<String>().every(
          (value) => !value.contains('.') && !value.contains(':'),
        ),
        isTrue,
      );
    });
  });
}
