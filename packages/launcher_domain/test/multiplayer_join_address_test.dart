import 'dart:convert';

import 'package:launcher_domain/launcher_domain.dart';
import 'package:test/test.dart';

import 'multiplayer_fixtures.dart';
import 'multiplayer_join_support.dart';

/// A hand-typed address names no server: the join learns it from the first
/// descriptor, and the known-hosts record catches a server that changed.
void main() {
  const otherServer =
      'cf82f7bc6b072f456554dd30c31b12fe5af423bbe03ff908124a1a08f0f16443';
  final when = DateTime.utc(2026, 10, 14, 12);

  group('typed addresses', () {
    test('read host, host:port, bracketed and bare IPv6 input', () {
      const cases = {
        '192.168.1.20': ('192.168.1.20', 27250),
        '192.168.1.20:27251': ('192.168.1.20', 27251),
        ' Play.Example.ORG:4000 ': ('play.example.org', 4000),
        'host.3com': ('host.3com', 27250),
        '[::1]': ('::1', 27250),
        '[2001:DB8::1]:1': ('2001:db8::1', 1),
        '[::ffff:10.0.0.7]:5': ('10.0.0.7', 5),
        'fe80::1': ('fe80::1', 27250),
      };
      cases.forEach((input, expected) {
        final target = JoinAddressTarget.tryParse(input);
        expect(target, isNotNull, reason: input);
        expect((target!.host, target.port), expected, reason: input);
        expect(target.pinnedServerId, isNull);
        expect(target.resolvesThroughService, isFalse);
        expect(target.toString(), isNot(contains(target.host)));
      });
      expect(
        JoinAddressTarget.tryParse('example.org', defaultPort: 9)!.port,
        9,
      );
      for (final input in [
        null,
        '',
        ':27250',
        'host:',
        'host:0',
        'host:65536',
        'host:080',
        'host:port',
        '[::1',
        '[::1]x',
        '[192.168.1.20]:5',
        '1.2.3',
        'bad_host',
        '[]:5',
        'a b:5',
        'x' * 300,
      ]) {
        expect(JoinAddressTarget.tryParse(input), isNull, reason: input);
      }
    });

    test('are validated without repeating the address', () {
      expect(
        () => JoinAddressTarget(host: '10.0.0.7', port: 0),
        throwsRangeError,
      );
      expect(
        JoinAddressTarget(host: '::FFFF:10.0.0.7', port: 1).host,
        '10.0.0.7',
      );
    });
  });

  group('joining a typed address', () {
    late VerifiedServerDescriptor server;
    late VerifiedServerDescriptor other;
    setUpAll(() async {
      server = await verifiedForTest(testDescriptor());
      final otherKeys = readMultiplayerFixture('descriptor/cases.json');
      final hostB = asObjects(otherKeys['keys'])[1];
      other = await verifiedForTest(
        ServerDescriptorV1(
          signingKey: WireText.tryParseBase64Url(
            hostB['signingKey']! as String,
            32,
          )!,
          transportKey: WireText.tryParseBase64Url(
            hostB['transportKey']! as String,
            32,
          )!,
          issuedAt: 0,
          name: 'Other',
          gameBuild: '2545',
          runtimeVersion: '0.2.0',
          protocolVersion: '1.0.0',
          maxPlayers: 2,
          passwordRequired: false,
          voice: false,
          visibility: ServerVisibility.lan,
          hostServesPackages: false,
          gamemode: null,
          world: null,
          packages: const [],
        ),
      );
      expect(other.serverId, otherServer);
    });

    final target = JoinAddressTarget(host: '192.168.1.20', port: 27250);

    test('learns the server from its first descriptor', () {
      var state = startJoin(target);
      expect(state.stage, JoinStage.fetchingDescriptor);
      expect(state.serverId, isNull);
      expect(state.descriptorRequest!.expectedServerId, isNull);
      state = JoinSession.reduce(state, JoinDescriptorVerified(server));
      expect(state.stage, JoinStage.checking);
      expect(state.serverId, hostAServerId);
    });

    test('ends on a changed identity the data layer reports, naming the '
        'server it served', () {
      final ended = JoinSession.reduce(
        startJoin(target),
        JoinFailed.identityChanged(servedServerId: otherServer),
      );
      final failure = ended.outcome!.failure!;
      expect(failure.code, JoinFailureCode.identityChanged);
      expect(failure.stage, JoinStage.fetchingDescriptor);
      expect(failure.servedServerId, otherServer);
      expect(
        () => JoinFailed(JoinFailureCode.identityChanged),
        throwsArgumentError,
        reason: 'an identity change always names the served server',
      );
      expect(
        () => JoinFailed.identityChanged(servedServerId: '10.0.0.7'),
        throwsA(
          isA<ArgumentError>().having(
            (error) => error.toString(),
            'text',
            isNot(contains('10.0.0.7')),
          ),
        ),
      );
    });

    test('trusting a changed identity accepts that server and no other', () {
      // The player saw the failure and chose to trust the server it named.
      final trusted = startJoin(target, trustedServerId: otherServer);
      expect(trusted.serverId, otherServer);
      expect(trusted.descriptorRequest!.expectedServerId, otherServer);
      final accepted = JoinSession.reduce(
        trusted,
        JoinDescriptorVerified(other),
      );
      expect(accepted.stage, JoinStage.checking);
      final impostor = JoinSession.reduce(
        trusted,
        JoinDescriptorVerified(server),
      ).outcome!.failure!;
      expect(impostor.code, JoinFailureCode.identityChanged);
      expect(impostor.servedServerId, hostAServerId);
      for (final (target, id) in [
        (JoinLinkTarget(SessionLink.server(hostAServerId)), otherServer),
        (target, 'not a server id'),
      ]) {
        expect(
          () => startJoin(target, trustedServerId: id),
          throwsArgumentError,
        );
      }
    });

    test(
      'reports another server after a re-plan as an identity change',
      () async {
        final plan = const JoinPlanner().plan(
          server: server,
          resolutions: const {},
          environment: testEnvironment,
          mayContactHost: true,
        );
        final replanned = [
          JoinDescriptorVerified(server),
          JoinPlanReady(plan),
          const JoinConsentGiven(),
          const JoinProfilePrepared(),
          const JoinGameStarted(),
          JoinFailed(JoinFailureCode.admissionChanged),
        ].fold(startJoin(target), JoinSession.reduce);
        expect(replanned.stage, JoinStage.fetchingDescriptor);
        expect(replanned.descriptorRequest!.expectedServerId, hostAServerId);
        final refused = JoinSession.reduce(
          replanned,
          JoinDescriptorVerified(other),
        ).outcome!.failure!;
        expect(refused.code, JoinFailureCode.identityChanged);
        expect(refused.servedServerId, otherServer);
        // A target that names its server reports the same as invalid.
        final linked = JoinSession.reduce(
          startJoin(
            JoinLinkTarget(
              SessionLink.direct(
                serverId: hostAServerId,
                host: '192.168.1.20',
                port: 27250,
              ),
            ),
          ),
          JoinDescriptorVerified(other),
        ).outcome!.failure!;
        expect(linked.code, JoinFailureCode.descriptorInvalid);
        expect(linked.servedServerId, isNull);
      },
    );
  });

  group('known hosts', () {
    final lan = JoinAddressTarget(host: '192.168.1.20', port: 27250);
    final named = JoinAddressTarget(host: 'Play.Example.ORG', port: 27250);

    test('key each target by the SHA-256 of host:port', () {
      expect(
        KnownHosts.targetKey('192.168.1.20', 27250),
        '3d2cf09e0c3c08b1c061117e74c8c88d20a864171d51591ffcc459dbc81c45f6',
      );
      expect(
        KnownHosts.targetKey('Play.Example.ORG', 27250),
        '8425fd1c2a0ffc811ea9bf1f38d807bc226c5d745eadac1ba54e30f382d194ec',
      );
      expect(
        KnownHosts.targetKey('2001:DB8:0:0:0:0:0:1', 27250),
        'b581abb7488c2b1b8585c42a3a18f68240573e3ff44df0cda1c0fdc4dcdf1cdb',
      );
      expect(() => KnownHosts.targetKey('host', 0), throwsRangeError);
    });

    test('trust on first use and report a changed server', () {
      var hosts = KnownHosts.empty();
      expect(hosts.check(lan, hostAServerId), KnownHostCheck.unknown);
      hosts = hosts.remember(lan, hostAServerId, when);
      expect(hosts.check(lan, hostAServerId), KnownHostCheck.match);
      expect(hosts.check(lan, otherServer), KnownHostCheck.mismatch);
      expect(hosts.check(named, hostAServerId), KnownHostCheck.unknown);
      hosts = hosts.remember(lan, otherServer, when);
      expect(hosts.check(lan, otherServer), KnownHostCheck.match);
      expect(hosts.length, 1);
      expect(hosts.forget(lan).check(lan, otherServer), KnownHostCheck.unknown);
      expect(() => hosts.remember(lan, 'A' * 64, when), throwsArgumentError);
    });

    test('keep the 256 most recent targets', () {
      var hosts = KnownHosts.empty();
      for (var port = 1; port <= 257; port++) {
        hosts = hosts.remember(
          JoinAddressTarget(host: '10.0.0.1', port: port),
          hostAServerId,
          when,
        );
      }
      expect(hosts.length, KnownHosts.maximumEntries);
      expect(
        hosts.check(
          JoinAddressTarget(host: '10.0.0.1', port: 1),
          hostAServerId,
        ),
        KnownHostCheck.unknown,
      );
      expect(
        hosts.check(
          JoinAddressTarget(host: '10.0.0.1', port: 2),
          hostAServerId,
        ),
        KnownHostCheck.match,
      );
    });

    test("write the game's file, which never holds an address", () {
      final hosts = KnownHosts.empty().remember(lan, hostAServerId, when);
      final text = utf8.decode(hosts.encode());
      expect(
        text,
        '{"hosts":[{"seenAt":${when.millisecondsSinceEpoch ~/ 1000},'
        '"serverId":"$hostAServerId","target":'
        '"3d2cf09e0c3c08b1c061117e74c8c88d20a864171d51591ffcc459dbc81c45f6"'
        '}],"schema":"topiaforge.known-hosts/1"}',
      );
      expect(text, isNot(contains('192.168')));
      final read = KnownHosts.decode(hosts.encode());
      expect(read.check(lan, hostAServerId), KnownHostCheck.match);
    });

    test('read an unreadable file as empty and skip bad entries', () {
      for (final bytes in [
        null,
        const <int>[],
        textBytes('{"hosts":[],"schema":"topiaforge.known-hosts/2"}'),
        textBytes('{"hosts":{},"schema":"topiaforge.known-hosts/1"}'),
        textBytes('{"hosts": [],"schema":"topiaforge.known-hosts/1"}'),
      ]) {
        expect(KnownHosts.decode(bytes).length, 0);
      }
      final key = KnownHosts.targetKey('192.168.1.20', 27250);
      final mixed = textBytes(
        '{"hosts":['
        '{"seenAt":1,"serverId":"$otherServer","target":"$key"},'
        '{"seenAt":2,"serverId":"XYZ","target":"$key"},'
        '{"seenAt":3,"serverId":"$hostAServerId"},'
        '{"seenAt":-4,"serverId":"$hostAServerId","target":"$key"}'
        '],"schema":"topiaforge.known-hosts/1"}',
      );
      final read = KnownHosts.decode(mixed);
      expect(read.length, 1);
      expect(read.check(lan, hostAServerId), KnownHostCheck.match);
      expect(utf8.decode(read.encode()), contains('"seenAt":0'));
    });
  });
}
