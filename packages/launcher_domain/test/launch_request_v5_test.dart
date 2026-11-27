import 'package:launcher_domain/launcher_domain.dart';
import 'package:test/test.dart';

import 'multiplayer_join_support.dart';

// V4 as the launcher wrote it before request V5 existed, captured from the
// unchanged writer. Only host-session and join-session launches may write
// anything else.
const _v4Target =
    '{"schemaVersion":4,"profileId":"default","profileRevision":7,'
    '"requestId":"0123456789abcdef0123456789abcdef","command":"launch-target",'
    '"safeMode":false,"inheritManagerModState":false,'
    '"enabledMods":["example.alpha","example.beta"],'
    '"selectedVersions":{"example.alpha":"1.2.0","example.beta":"0.3.0-rc.1+build.7"},'
    '"packages":[{"id":"example.alpha","version":"1.2.0"},'
    '{"id":"example.beta","version":"0.3.0-rc.1+build.7"}],'
    '"digest":"d64fc177cff1cde2","plan":{"targetId":"example.alpha.play",'
    '"gamemodeId":"example.alpha.mode","worldId":"example.alpha.city.east",'
    '"worldFamilyId":"example.alpha.city","transition":"scene-replacement",'
    '"request":{"targetId":"example.alpha.play","worldOverride":"example.alpha.city.east"},'
    '"packages":[{"id":"example.alpha","version":"1.2.0"},'
    '{"id":"example.beta","version":"0.3.0-rc.1+build.7"}],'
    '"digest":"d64fc177cff1cde2"}}';
const _v4Menu =
    '{"schemaVersion":4,"profileId":"default","profileRevision":7,'
    '"requestId":"fedcba9876543210fedcba9876543210","command":"main-menu",'
    '"safeMode":false,"inheritManagerModState":true,'
    '"enabledMods":["example.alpha"],"selectedVersions":{"example.gamma":"2.0.0"},'
    '"packages":[{"id":"example.alpha","version":"1.2.0"},'
    '{"id":"example.beta","version":"0.3.0-rc.1+build.7"}],'
    '"digest":"d64fc177cff1cde2"}';
const _v4Safe =
    '{"schemaVersion":4,"profileId":"default","profileRevision":7,'
    '"requestId":"aaaabbbbccccdddd","command":"main-menu","safeMode":true,'
    '"inheritManagerModState":false,"enabledMods":["example.alpha"],'
    '"selectedVersions":{},"packages":[],"digest":"cbf29ce484222325"}';

final _packages = [
  PackageIdentity(id: 'example.alpha', version: '1.2.0'),
  PackageIdentity(id: 'example.beta', version: '0.3.0-rc.1+build.7'),
];

ProfileLaunchConfigurationV4 _target() => ProfileLaunchConfigurationV4(
  profileId: 'default',
  profileRevision: 7,
  requestId: '0123456789abcdef0123456789abcdef',
  command: 'launch-target',
  safeMode: false,
  inheritManagerModState: false,
  enabledMods: const ['example.beta', 'example.alpha'],
  selectedVersions: const {
    'example.beta': '0.3.0-rc.1+build.7',
    'example.alpha': '1.2.0',
  },
  packages: _packages,
  plan: LaunchPlanDescriptor(
    targetId: 'example.alpha.play',
    gamemodeId: 'example.alpha.mode',
    worldId: 'example.alpha.city.east',
    worldFamilyId: 'example.alpha.city',
    transition: 'scene-replacement',
    request: LaunchRequest(
      targetId: 'example.alpha.play',
      worldOverride: 'example.alpha.city.east',
    ),
    packages: _packages,
  ),
);

ProfileLaunchConfigurationV4 _menu({bool inherit = true}) =>
    ProfileLaunchConfigurationV4(
      profileId: 'default',
      profileRevision: 7,
      requestId: 'fedcba9876543210fedcba9876543210',
      command: 'main-menu',
      safeMode: false,
      inheritManagerModState: inherit,
      enabledMods: inherit
          ? const ['example.alpha']
          : const ['example.alpha', 'example.beta'],
      selectedVersions: inherit
          ? const {'example.gamma': '2.0.0'}
          : const {'example.alpha': '1.2.0'},
      packages: _packages,
    );

ProfileLaunchConfigurationV4 _safe() => ProfileLaunchConfigurationV4(
  profileId: 'default',
  profileRevision: 7,
  requestId: 'aaaabbbbccccdddd',
  command: 'main-menu',
  safeMode: true,
  inheritManagerModState: false,
  enabledMods: const ['example.alpha'],
  selectedVersions: const {},
  packages: const [],
);

final _acknowledged = MultiplayerSettings(
  displayName: 'Sam',
).acknowledgePrivacyDisclosure();

LaunchSessionTarget _lanListing() => LaunchListingTarget(
  source: ServerListingSource.lan,
  serverId: hostAServerId,
  descriptorSha256: 'a' * 64,
  packageDigest: 'b' * 64,
);

LaunchSessionRequest _join({
  LaunchSessionTarget? target,
  SessionPrivacy privacy = SessionPrivacy.relayOnly,
  String? serviceOrigin,
  MultiplayerSettings? settings,
}) => LaunchSessionRequest.join(
  intentId: 'intent-1',
  settings: settings ?? _acknowledged,
  privacy: privacy,
  target: target ?? _lanListing(),
  serviceOrigin: serviceOrigin,
);

LaunchSessionRequest _host({
  SessionPrivacy privacy = SessionPrivacy.relayOnly,
  MultiplayerSettings? settings,
}) => LaunchSessionRequest.host(
  intentId: 'intent-1',
  settings: settings ?? _acknowledged,
  privacy: privacy,
  host: LaunchHostOptions(name: "Sam's Sandbox"),
);

LaunchDirectTarget _direct(String host, [int port = 47613]) =>
    LaunchDirectTarget(
      host: host,
      port: port,
      serverId: hostAServerId,
      descriptorSha256: 'a' * 64,
      packageDigest: 'b' * 64,
    );

void main() {
  group('the V4 and V5 selection', () {
    test('every launch without a session stages V4 byte for byte', () {
      for (final (profile, golden) in [
        (_target(), _v4Target),
        (_menu(), _v4Menu),
        (_safe(), _v4Safe),
      ]) {
        final staged = StagedLaunchRequest.forLaunch(profile);
        expect(staged, isA<StagedV4LaunchRequest>());
        expect(staged.schemaVersion, 4);
        expect(staged.encode(), golden);
        expect(LaunchTransport.writeProfile(profile), golden);
        expect(staged.session, isNull);
        expect(staged.command, profile.command);
        final read = StagedLaunchRequest.decode(golden);
        expect(read, isA<StagedV4LaunchRequest>());
        expect(read.encode(), golden);
      }
    });

    test('a session launch stages V5 over the same profile', () {
      final host = StagedLaunchRequest.forLaunch(_target(), session: _host());
      expect(host, isA<StagedV5LaunchRequest>());
      expect(host.schemaVersion, 5);
      expect(host.command, 'host-session');
      expect(host.executable.command, 'launch-target');
      expect(host.executable.plan!.toJson(), _target().plan!.toJson());
      expect(host.digest, _target().digest);
      expect(host.encode(), startsWith('{"command":"host-session",'));
      expect(StagedLaunchRequest.decode(host.encode()).encode(), host.encode());

      final join = StagedLaunchRequest.forLaunch(
        _menu(inherit: false),
        session: _join(),
      );
      expect(join.command, 'join-session');
      expect(join.executable.command, 'main-menu');
      expect(join.executable.safeMode, isFalse);
      expect(join.session!.role, LaunchSessionRole.join);
    });

    test('safe mode never hosts or joins', () {
      expect(
        () => StagedLaunchRequest.forLaunch(_safe(), session: _host()),
        throwsFormatException,
      );
    });

    test('a join carries no plan and inherits nothing', () {
      expect(
        () => StagedLaunchRequest.forLaunch(_target(), session: _join()),
        throwsFormatException,
      );
      expect(
        () => StagedLaunchRequest.forLaunch(_menu(), session: _join()),
        throwsFormatException,
      );
      expect(
        StagedLaunchRequest.forLaunch(_menu(), session: _host()).command,
        'host-session',
      );
    });

    test('the V4 reader refuses V5 and the V5 reader refuses V4', () {
      final v5 = StagedLaunchRequest.forLaunch(
        _menu(inherit: false),
        session: _join(),
      ).encode();
      expect(() => LaunchTransport.readProfile(v5), throwsFormatException);
      expect(
        () => LaunchTransport.readProfileV5(_v4Menu),
        throwsFormatException,
      );
    });
  });

  group('the session object', () {
    test('a relay-only host maps its port only when the player allows', () {
      final host = _host(settings: _acknowledged.withUpnp(true));
      expect(host.transport.privacy, MultiplayerAddressPrivacy.relayOnly);
      expect(host.transport.directConsent, isNull);
      expect(host.transport.upnp, isTrue);
      expect(host.transport.lan, isTrue);
      expect(host.transport.port, LanBeacon.defaultGamePort);
      expect(host.displayName, 'Sam');
      expect(host.target, isNull);
    });

    test('a join never maps a port and binds any free one', () {
      final join = _join(settings: _acknowledged.withUpnp(true));
      expect(join.transport.upnp, isFalse);
      expect(join.transport.port, 0);
      expect(join.host, isNull);
    });

    test('direct privacy carries consent for its own session', () {
      final direct = _acknowledged.privacyForSession(allowDirect: true);
      final host = _host(privacy: direct);
      expect(host.transport.allowsDirect, isTrue);
      expect(host.transport.directConsent!.intentId, host.intentId);
      expect(
        host.transport.directConsent!.disclosure,
        MultiplayerSettings.currentPrivacyDisclosure,
      );
      final read = LaunchSessionRequest.fromJson(host.toJson());
      expect(read.transport.directConsent!.intentId, 'intent-1');
    });

    test('relay-only allows direct targets only on the local network', () {
      for (final host in ['192.168.1.20', '10.0.0.7', 'fd00::7', '::1']) {
        expect(_join(target: _direct(host)).target, isA<LaunchDirectTarget>());
      }
      for (final host in ['localhost', 'gameroom.local', 'peer.localhost']) {
        expect(_join(target: _direct(host)).target, isA<LaunchDirectTarget>());
      }
      for (final host in ['203.0.113.7', '100.64.0.1', 'play.example.org']) {
        expect(() => _join(target: _direct(host)), throwsFormatException);
        final direct = _acknowledged.privacyForSession(allowDirect: true);
        expect(
          _join(target: _direct(host), privacy: direct).transport.allowsDirect,
          isTrue,
        );
      }
    });

    test('invites and Internet listings need the service', () {
      final invite = LaunchInviteTarget(
        inviteCode: 'ABCDE12345',
        serverId: hostAServerId,
        descriptorSha256: 'a' * 64,
        packageDigest: 'b' * 64,
      );
      expect(() => _join(target: invite), throwsFormatException);
      expect(
        _join(
          target: invite,
          serviceOrigin: 'https://mp.topiaforge.dev',
        ).serviceOrigin,
        'https://mp.topiaforge.dev',
      );
    });

    test('a LAN listing needs LAN discovery on', () {
      expect(
        () => _join(settings: _acknowledged.withLanDiscovery(false)),
        throwsFormatException,
      );
    });

    test('the service origin is an HTTPS DNS name and an optional port', () {
      final invite = LaunchInviteTarget(
        inviteCode: 'ABCDE12345',
        serverId: hostAServerId,
        descriptorSha256: 'a' * 64,
        packageDigest: 'b' * 64,
      );
      for (final origin in [
        'https://mp.topiaforge.dev',
        'https://localhost:8787',
        'https://a-b.example.org:65535',
        'https://0x7f.example.org',
        'https://example.0x7g',
      ]) {
        expect(
          _join(target: invite, serviceOrigin: origin).serviceOrigin,
          origin,
        );
        expect(LaunchSessionRequest.isServiceOrigin(origin), isTrue);
      }
      for (final origin in [
        'http://mp.topiaforge.dev',
        'https://203.0.113.5',
        'https://[::1]',
        // Names the URL Standard and the system resolver read as addresses.
        'https://0x7f000001',
        'https://0X7F000001',
        'https://0x',
        'https://lan.0x1f',
        'https://play.example.42',
        'https://127.1',
        'https://2130706433',
        'https://MP.topiaforge.dev',
        'https://mp.topiaforge.dev/',
        'https://mp.topiaforge.dev:0',
        'https://mp.topiaforge.dev:443',
        'https://mp.topiaforge.dev:08787',
        'https://mp.topiaforge.dev:65536',
        'https://user@mp.topiaforge.dev',
        'https://mp.topiaforge.dev?x=1',
        'https://',
      ]) {
        expect(
          () => _join(target: invite, serviceOrigin: origin),
          throwsFormatException,
          reason: origin,
        );
        expect(LaunchSessionRequest.isServiceOrigin(origin), isFalse);
      }
    });

    test('only developer mode serves packages', () {
      final settings = MultiplayerSettings(displayName: 'Sam');
      LaunchSessionRequest host({required bool developerMode}) =>
          LaunchSessionRequest.host(
            intentId: 'intent-dev',
            settings: settings,
            privacy: SessionPrivacy.relayOnly,
            host: LaunchHostOptions(name: 'Dev', servePackages: true),
            developerMode: developerMode,
          );
      expect(() => host(developerMode: false), throwsFormatException);
      final serving = host(developerMode: true);
      expect(serving.developerMode, isTrue);
      expect(serving.toJson()['developerMode'], isTrue);
      expect(
        LaunchSessionRequest.fromJson(serving.toJson()).host!.servePackages,
        isTrue,
      );
      final json = serving.toJson()..['developerMode'] = false;
      expect(() => LaunchSessionRequest.fromJson(json), throwsFormatException);
    });
  });

  group('join targets from a plan', () {
    late JoinPlan plan;

    setUpAll(() async {
      plan = await planJoin([
        (
          testPackage('example.sandbox'),
          availableOf(testManifest('example.sandbox')),
        ),
      ]);
    });

    test('every join target pins the planned server', () {
      final server = plan.server;
      for (final (target, kind) in [
        (JoinLinkTarget(SessionLink.invite('ABCDE12345')), 'invite'),
        (JoinLinkTarget(SessionLink.server(hostAServerId)), 'listing'),
        (
          JoinLinkTarget(
            SessionLink.direct(
              serverId: hostAServerId,
              host: '192.168.1.20',
              port: 27250,
            ),
          ),
          'direct',
        ),
        (
          JoinListingTarget(
            serverId: hostAServerId,
            source: ServerListingSource.lan,
          ),
          'listing',
        ),
        (JoinAddressTarget(host: '10.0.0.9', port: 27251), 'direct'),
      ]) {
        final value = LaunchSessionTarget.forPlan(target, plan);
        expect(value.kind, kind);
        expect(value.serverId, server.serverId);
        expect(value.descriptorSha256, server.descriptorSha256);
        expect(value.packageDigest, server.packageDigest);
        expect(
          LaunchSessionTarget.fromJson(value.toJson()).toJson(),
          value.toJson(),
        );
      }
      final internet = LaunchSessionTarget.forPlan(
        JoinLinkTarget(SessionLink.server(hostAServerId)),
        plan,
      );
      expect(
        (internet as LaunchListingTarget).source,
        ServerListingSource.internet,
      );
      final typed =
          LaunchSessionTarget.forPlan(
                JoinAddressTarget(host: '10.0.0.9', port: 27251),
                plan,
              )
              as LaunchDirectTarget;
      expect((typed.host, typed.port), ('10.0.0.9', 27251));
    });

    test('a plan for another server is refused', () {
      expect(
        () => LaunchSessionTarget.forPlan(
          JoinLinkTarget(SessionLink.server('c' * 64)),
          plan,
        ),
        throwsArgumentError,
      );
    });
  });
}
