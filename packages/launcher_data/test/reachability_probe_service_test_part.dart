part of 'reachability_probe_test.dart';

/// Settings persistence, the policy gates, and one local run.
///
/// Split out of the suite's own file to stay inside the 500-line cap in AGENTS.md.
void _reachabilityProbeServiceTests() {
  group('ReachabilityProbeService', () {
    late Directory root;
    final requestedFamilies = <InternetAddressType>[];

    setUp(() async {
      root = await Directory.systemTemp.createTemp('topiaforge-probe-');
      requestedFamilies.clear();
    });

    tearDown(() async {
      if (await root.exists()) await root.delete(recursive: true);
    });

    ReachabilityProbeService service({StunTransport? transport}) =>
        ReachabilityProbeService(
          dataRoot: root.path,
          openTransport: transport == null
              ? null
              : (family) async {
                  requestedFamilies.add(family);
                  return transport;
                },
        );

    test('defaults to off with no settings file', () async {
      expect(await service().loadSettings(), const ReachabilityProbeSettings());
    });

    test('treats a corrupt settings file as off', () async {
      await File(
        p.join(root.path, ReachabilityProbeService.settingsFileName),
      ).writeAsString('{not json');

      expect(await service().loadSettings(), const ReachabilityProbeSettings());
    });

    test('persists the opt-in', () async {
      final subject = service();
      await subject.saveSettings(
        const ReachabilityProbeSettings(enabled: true),
      );

      expect(
        await subject.loadSettings(),
        const ReachabilityProbeSettings(enabled: true),
      );
      final written = jsonDecode(
        await File(
          p.join(root.path, ReachabilityProbeService.settingsFileName),
        ).readAsString(),
      );
      expect(written, {'enabled': true, 'shareAggregateResults': false});
    });

    test('refuses to run outside developer mode', () async {
      final transport = _FakeStunTransport(mappedFor: const {});
      final subject = service(transport: transport);
      await subject.saveSettings(
        const ReachabilityProbeSettings(enabled: true),
      );

      final outcome = await subject.run(
        developerMode: false,
        servers: const ['198.51.100.10:3478'],
      );

      expect(outcome.refusal, ReachabilityProbeRefusal.developerModeRequired);
      expect(outcome.ran, isFalse);
      expect(
        transport.calls,
        isEmpty,
        reason: 'A refused probe must not touch the network at all.',
      );
    });

    test('refuses to run until the player opts in', () async {
      final transport = _FakeStunTransport(mappedFor: const {});

      final outcome = await service(
        transport: transport,
      ).run(developerMode: true, servers: const ['198.51.100.10:3478']);

      expect(outcome.refusal, ReachabilityProbeRefusal.notEnabled);
      expect(transport.calls, isEmpty);
    });

    test('reports unavailable with no configured servers', () async {
      final subject = service(
        transport: _FakeStunTransport(mappedFor: const {}),
      );
      await subject.saveSettings(
        const ReachabilityProbeSettings(enabled: true),
      );

      final outcome = await subject.run(developerMode: true, servers: const []);

      expect(outcome.refusal, isNull);
      expect(outcome.message, contains('No probe servers'));
    });

    test('runs and closes the transport once permitted', () async {
      final reflexive = _v4([192, 0, 2, 55], 51234);
      final transport = _FakeStunTransport(
        mappedFor: {
          primary: reflexive,
          alternateOnPrimaryPort: reflexive,
          alternate: reflexive,
        },
        otherAddress: alternate,
      );
      final subject = service(transport: transport);
      await subject.saveSettings(
        const ReachabilityProbeSettings(enabled: true),
      );

      final outcome = await subject.run(
        developerMode: true,
        servers: const ['198.51.100.10:3478'],
      );

      expect(outcome.ran, isTrue);
      expect(
        outcome.classification.reachability,
        HostReachability.holePunchable,
      );
      expect(transport.closed, isTrue);
      expect(requestedFamilies, [InternetAddressType.IPv4]);
    });

    test('binds for the family the configured servers name', () async {
      final transport = _FakeStunTransport(mappedFor: const {});
      final subject = service(transport: transport);
      await subject.saveSettings(
        const ReachabilityProbeSettings(enabled: true),
      );

      await subject.run(
        developerMode: true,
        servers: const ['[2001:db8::1]:3478'],
      );

      expect(requestedFamilies, [InternetAddressType.IPv6]);
    });

    test('refuses a server list that mixes address families', () async {
      // One socket serves the whole run and cannot leave its family, and a mapping verdict built from
      // endpoints in two families would be meaningless even if it could.
      final transport = _FakeStunTransport(mappedFor: const {});
      final subject = service(transport: transport);
      await subject.saveSettings(
        const ReachabilityProbeSettings(enabled: true),
      );

      final outcome = await subject.run(
        developerMode: true,
        servers: const ['198.51.100.10:3478', '[2001:db8::1]:3478'],
      );

      expect(outcome.ran, isFalse);
      expect(outcome.message, contains('same address family'));
      expect(requestedFamilies, isEmpty);
      expect(transport.calls, isEmpty);
    });

    test('never previews a report while the privacy notice is unapproved', () {
      // Consent from the player is necessary but not sufficient; the project's own approval is still missing.
      final preview = service().reportPreview(
        developerMode: true,
        settings: const ReachabilityProbeSettings(
          enabled: true,
          shareAggregateResults: true,
        ),
        classification: NatClassification.unknown,
      );

      expect(preview, ReachabilityProbeRefusal.privacyNoticeNotApproved);
    });
  });
}
