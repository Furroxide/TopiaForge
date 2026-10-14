import 'package:launcher_domain/launcher_domain.dart';
import 'package:test/test.dart';

import 'multiplayer_fixtures.dart';
import 'multiplayer_join_support.dart';

/// Developer-mode archives come from the host itself, so relay-only joins
/// download them only from a host on the local network, and the download
/// step carries the same contact rules as the descriptor fetch.
void main() {
  late VerifiedServerDescriptor server;
  final sandbox = testManifest('example.sandbox');
  final environment = environmentWith(developerMode: true);
  final serverLink = JoinLinkTarget(SessionLink.server(hostAServerId));
  final listing = JoinListingTarget(
    serverId: hostAServerId,
    source: ServerListingSource.internet,
  );
  final lanLink = JoinLinkTarget(
    SessionLink.direct(
      serverId: hostAServerId,
      host: '192.168.1.20',
      port: 27250,
    ),
  );

  setUpAll(() async {
    server = await verifiedForTest(
      testDescriptor(
        packages: [
          testPackage('example.local'),
          testPackage('example.sandbox'),
        ],
        hostServesPackages: true,
      ),
    );
  });

  JoinPlan planFor(JoinSessionState state) => const JoinPlanner().plan(
    server: server,
    resolutions: {
      testSha('example.local'): JoinPackageResolution.hostOffered(),
      testSha('example.sandbox'): availableOf(sandbox),
    },
    environment: environment,
    mayContactHost: state.mayContactHost,
  );

  /// The join after its descriptor verified, resolving first when it must.
  JoinSessionState checking(JoinSessionState state) => [
    if (state.stage == JoinStage.resolving) JoinTargetResolved(hostAServerId),
    JoinDescriptorVerified(server),
  ].fold(state, JoinSession.reduce);

  test('relay-only never downloads from a host it cannot place', () {
    for (final target in [serverLink, listing]) {
      final state = checking(startJoin(target));
      expect(state.mayContactHost, isFalse);
      final plan = planFor(state);
      expect(plan.isJoinable, isFalse);
      final blocker = plan.blockers.single;
      expect(blocker.code, JoinBlockerCode.directNotAllowed);
      expect(blocker.modId, 'example.local');
      final ended = JoinSession.reduce(state, JoinPlanReady(plan));
      expect(ended.outcome!.failure!.code, JoinFailureCode.directNotAllowed);
      expect(ended.outcome!.failure!.stage, JoinStage.checking);
    }
  });

  test('a plan made as if the host could be reached cannot pass', () {
    // The probe: relay-only, a server link, a host that serves packages,
    // and a plan that wrongly offers its archive.
    final state = checking(startJoin(serverLink));
    final wrong = const JoinPlanner().plan(
      server: server,
      resolutions: {
        testSha('example.local'): JoinPackageResolution.hostOffered(),
        testSha('example.sandbox'): availableOf(sandbox),
      },
      environment: environment,
      mayContactHost: true,
    );
    expect(wrong.hasUnverifiedPackages, isTrue);
    final ended = JoinSession.reduce(state, JoinPlanReady(wrong));
    expect(ended.stage, JoinStage.ended);
    expect(ended.outcome!.failure!.code, JoinFailureCode.directNotAllowed);
    expect(ended.outcome!.failure!.stage, JoinStage.checking);
    expect(ended.downloadRequest, isNull);
  });

  test('a host on the local network, or a direct session, may serve them', () {
    for (final (target, privacy) in [
      (lanLink, MultiplayerAddressPrivacy.relayOnly),
      (
        JoinListingTarget(
          serverId: hostAServerId,
          source: ServerListingSource.lan,
        ),
        MultiplayerAddressPrivacy.relayOnly,
      ),
      (serverLink, MultiplayerAddressPrivacy.direct),
    ]) {
      final state = checking(startJoin(target, privacy: privacy));
      expect(state.mayContactHost, isTrue, reason: '$target');
      final plan = planFor(state);
      expect(plan.hasUnverifiedPackages, isTrue);
      final downloading = [
        JoinPlanReady(plan),
        const JoinConsentGiven(hostServedAcknowledged: true),
      ].fold(state, JoinSession.reduce);
      expect(downloading.stage, JoinStage.downloading, reason: '$target');
    }
  });

  test('downloading carries the contact rules the descriptor fetch had', () {
    final fetching = startJoin(lanLink);
    final descriptorRules = fetching.descriptorRequest!;
    final downloading = [
      JoinDescriptorVerified(server),
    ].fold(fetching, JoinSession.reduce);
    final plan = planFor(downloading);
    final state = [
      JoinPlanReady(plan),
      const JoinConsentGiven(hostServedAcknowledged: true),
    ].fold(downloading, JoinSession.reduce);
    expect(state.descriptorRequest, isNull);
    final request = state.downloadRequest!;
    expect(request.target, same(descriptorRules.target));
    expect(request.privacy, descriptorRules.privacy);
    expect(request.allowsDirect, isFalse);
    expect(request.serverId, hostAServerId);
    expect(request.hostDownloads.map((item) => item.id), ['example.local']);
    expect(request.registryDownloads.map((item) => item.modId), [
      'example.sandbox',
    ]);
    expect(
      request.contactableAddresses(const [
        [8, 8, 8, 8],
        [192, 168, 1, 20],
      ]),
      [
        [192, 168, 1, 20],
      ],
    );
    expect(request.toString(), isNot(contains('192.168')));
    final installing = JoinSession.reduce(
      state,
      const JoinDownloadsCompleted(),
    );
    expect(installing.downloadRequest, isNull);
  });

  test('a host the rules forbid at download time ends the join', () {
    // The rules tell the data layer to report directNotAllowed when a local
    // name resolves to no local address, or a LAN listing's sender is off
    // the local network. The join must end rather than wait in downloading.
    final localName = JoinLinkTarget(
      SessionLink.direct(
        serverId: hostAServerId,
        host: 'printer.local',
        port: 27250,
      ),
    );
    final lanListing = JoinListingTarget(
      serverId: hostAServerId,
      source: ServerListingSource.lan,
    );
    for (final target in [localName, lanListing]) {
      final state = checking(startJoin(target));
      final downloading = [
        JoinPlanReady(planFor(state)),
        const JoinConsentGiven(hostServedAcknowledged: true),
      ].fold(state, JoinSession.reduce);
      expect(downloading.stage, JoinStage.downloading, reason: '$target');
      final request = downloading.downloadRequest!;
      expect(request.hostDownloads, isNotEmpty);
      expect(
        request.contactableAddresses(const [
          [203, 0, 113, 5],
        ]),
        isEmpty,
      );
      final ended = JoinSession.reduce(
        downloading,
        JoinFailed(
          JoinFailureCode.directNotAllowed,
          detail: 'The host is no longer on the local network.',
        ),
      );
      expect(ended.stage, JoinStage.ended, reason: '$target');
      final failure = ended.outcome!.failure!;
      expect(failure.code, JoinFailureCode.directNotAllowed);
      expect(failure.stage, JoinStage.downloading);
      expect(ended.downloadRequest, isNull);
    }
  });
}
