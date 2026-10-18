import 'package:launcher_domain/launcher_domain.dart';
import 'package:test/test.dart';

import 'multiplayer_fixtures.dart';
import 'multiplayer_join_support.dart';

/// The domain half of exact installs: the failures and the event the
/// content stages report, and the catalog's refusal to replace an installed
/// archive.
void main() {
  late VerifiedServerDescriptor server;
  late JoinPlan plan;
  final sandbox = testManifest('example.sandbox');
  final target = JoinLinkTarget(SessionLink.server(hostAServerId));

  setUpAll(() async {
    server = await verifiedForTest(
      testDescriptor(packages: [testPackage('example.sandbox')]),
    );
    plan = const JoinPlanner().plan(
      server: server,
      resolutions: {testSha('example.sandbox'): availableOf(sandbox)},
      environment: testEnvironment,
      mayContactHost: true,
    );
  });

  JoinSessionState run(JoinSessionState state, List<JoinEvent> events) =>
      events.fold(state, JoinSession.reduce);

  JoinSessionState downloading() => run(startJoin(target), [
    JoinTargetResolved(hostAServerId),
    JoinDescriptorVerified(server),
    JoinPlanReady(plan),
    const JoinConsentGiven(),
  ]);

  JoinSessionState installing() =>
      JoinSession.reduce(downloading(), const JoinDownloadsCompleted());

  group('the reducer', () {
    test('a plan needing host-served packages ends while downloading', () {
      final state = downloading();
      expect(state.stage, JoinStage.downloading);
      final ended = JoinSession.reduce(
        state,
        JoinFailed(
          JoinFailureCode.hostDownloadUnsupported,
          detail: 'Only the host serves example.local.',
        ),
      );
      expect(ended.stage, JoinStage.ended);
      final failure = ended.outcome!.failure!;
      expect(failure.code, JoinFailureCode.hostDownloadUnsupported);
      expect(failure.stage, JoinStage.downloading);
      expect(failure.detail, 'Only the host serves example.local.');
      // It belongs to downloading alone.
      final later = installing();
      expect(
        JoinSession.reduce(
          later,
          JoinFailed(JoinFailureCode.hostDownloadUnsupported),
        ),
        same(later),
      );
    });

    test('a running game ends an install, saying who owns it', () {
      for (final ownership in JoinGameOwnership.values) {
        final ended = JoinSession.reduce(
          installing(),
          JoinFailed.gameRunning(ownership: ownership, detail: 'Close it.'),
        );
        expect(ended.stage, JoinStage.ended);
        final failure = ended.outcome!.failure!;
        expect(failure.code, JoinFailureCode.gameRunning);
        expect(failure.stage, JoinStage.installing);
        expect(failure.gameOwnership, ownership);
        expect(failure.rollbackComplete, isNull);
      }
    });

    test('the consent says whether it covered closing the player\'s game', () {
      expect(installing().closeOwnedGameAcknowledged, isFalse);
      final covered = run(startJoin(target), [
        JoinTargetResolved(hostAServerId),
        JoinDescriptorVerified(server),
        JoinPlanReady(plan),
        const JoinConsentGiven(closeOwnedGameAcknowledged: true),
        const JoinDownloadsCompleted(),
      ]);
      expect(covered.stage, JoinStage.installing);
      expect(covered.closeOwnedGameAcknowledged, isTrue);
      // It is the player's consent, so it lasts through the steps after it.
      final progressed = JoinSession.reduce(
        covered,
        JoinInstallProgressed(packagesDone: 0, packagesTotal: 1),
      );
      expect(progressed.closeOwnedGameAcknowledged, isTrue);
      expect(
        JoinSession.reduce(
          progressed,
          const JoinInstallCompleted(),
        ).closeOwnedGameAcknowledged,
        isTrue,
      );
    });

    test('an install undone for a cancel request ends as cancelled', () {
      final held = JoinSession.reduce(
        installing(),
        const JoinCancelRequested(),
      );
      expect(held.stage, JoinStage.installing);
      expect(held.cancelRequested, isTrue);
      final ended = JoinSession.reduce(held, const JoinInstallUndone());
      expect(ended.stage, JoinStage.ended);
      expect(ended.outcome!.wasCancelled, isTrue);
      expect(ended.outcome!.failure!.stage, JoinStage.installing);
      expect(ended.cancelRequested, isFalse);
    });

    test('an install that finished before the cancel still ends cancelled', () {
      final held = JoinSession.reduce(
        installing(),
        const JoinCancelRequested(),
      );
      final ended = JoinSession.reduce(held, const JoinInstallCompleted());
      expect(ended.outcome!.wasCancelled, isTrue);
      expect(ended.stage, JoinStage.ended);
    });

    test(
      'an install undone unasked fails, rolled back, and never moves on',
      () {
        final ended = JoinSession.reduce(
          installing(),
          const JoinInstallUndone(),
        );
        expect(ended.stage, JoinStage.ended);
        final failure = ended.outcome!.failure!;
        expect(failure.code, JoinFailureCode.installFailed);
        expect(failure.rollbackComplete, isTrue);
        expect(failure.detail, contains('Nothing was installed'));
      },
    );

    test('an undone install is ignored outside installing', () {
      for (final state in [
        downloading(),
        run(installing(), const [JoinInstallCompleted()]),
      ]) {
        expect(
          JoinSession.reduce(state, const JoinInstallUndone()),
          same(state),
        );
      }
    });

    test('an install failure says whether its rollback completed', () {
      for (final complete in [true, false]) {
        final ended = JoinSession.reduce(
          installing(),
          JoinFailed.installFailed(rollbackComplete: complete),
        );
        expect(ended.outcome!.failure!.rollbackComplete, complete);
      }
    });

    test('install progress is kept while installing, and only then', () {
      expect(installing().installProgress, isNull);
      final counted = run(installing(), [
        JoinInstallProgressed(packagesDone: 0, packagesTotal: 2),
        JoinInstallProgressed(packagesDone: 1, packagesTotal: 2),
      ]);
      expect(counted.stage, JoinStage.installing);
      expect(counted.installProgress!.packagesDone, 1);
      expect(counted.installProgress!.fraction, 0.5);
      // Finishing the step, or ending the join, clears it.
      expect(
        JoinSession.reduce(
          counted,
          const JoinInstallCompleted(),
        ).installProgress,
        isNull,
      );
      expect(
        JoinSession.reduce(counted, const JoinInstallUndone()).installProgress,
        isNull,
      );
      // Outside installing it changes nothing.
      final early = downloading();
      expect(
        JoinSession.reduce(
          early,
          JoinInstallProgressed(packagesDone: 1, packagesTotal: 2),
        ),
        same(early),
      );
      expect(
        () => JoinInstallProgressed(packagesDone: 3, packagesTotal: 2),
        throwsArgumentError,
      );
      expect(
        () => JoinInstallProgressed(packagesDone: -1, packagesTotal: 2),
        throwsArgumentError,
      );
      expect(
        const JoinInstallProgress(packagesDone: 0, packagesTotal: 0).fraction,
        0,
      );
    });
  });

  group('an archive installed under the same id and version', () {
    final other = 'f' * 64;

    test('blocks the join before any source can offer the exact one', () {
      final catalog = ExactPackageCatalog(
        installed: [
          ExactInstalledArchive(
            sha256: other,
            manifest: testManifest('Example.Sandbox'),
          ),
        ],
        sources: [
          ExactSourcePackage(
            sourceId: 'io.github.furroxide.topiaforge.official',
            sourceName: 'TopiaForge Mod Registry',
            enabled: true,
            manifest: sandbox,
            downloadUrl: 'https://docs.topiaforge.dev/registry/sandbox.zip',
            sha256: testSha('example.sandbox'),
          ),
        ],
      );
      final resolution = catalog.resolvePackage(
        testPackage('example.sandbox'),
        developerMode: true,
        hostServesPackages: true,
      );
      expect(
        resolution.availability,
        JoinPackageAvailability.installedAsOtherArchive,
      );
      expect(resolution.manifest, isNull);
      final blocked = const JoinPlanner().planFromCatalog(
        server: server,
        catalog: catalog,
        environment: testEnvironment,
        mayContactHost: true,
      );
      expect(blocked.isJoinable, isFalse);
      final blocker = blocked.blockers.single;
      expect(blocker.code, JoinBlockerCode.contentUnavailable);
      expect(blocker.modId, 'example.sandbox');
      expect(blocker.message, contains('joining never replaces your copy'));
      expect(blocker.message, contains('then join again'));
    });

    test('says to remove the other server\'s content when it is that '
        'server\'s, which the player\'s Mods list does not show', () {
      final catalog = ExactPackageCatalog(
        installed: [
          ExactInstalledArchive(
            sha256: other,
            manifest: sandbox,
            serverOnly: true,
          ),
        ],
      );
      expect(
        catalog
            .resolvePackage(
              testPackage('example.sandbox'),
              developerMode: false,
              hostServesPackages: false,
            )
            .availability,
        JoinPackageAvailability.installedForAnotherServer,
      );
      final blocker = const JoinPlanner()
          .planFromCatalog(
            server: server,
            catalog: catalog,
            environment: testEnvironment,
            mayContactHost: true,
          )
          .blockers
          .single;
      expect(blocker.code, JoinBlockerCode.contentUnavailable);
      expect(blocker.message, contains("Remove that server's content"));
      // Uninstalling the mod in Mods would take the player's own versions.
      expect(blocker.message, isNot(contains('Mods')));
    });

    test('another installed version is no conflict', () {
      final catalog = ExactPackageCatalog(
        installed: [
          ExactInstalledArchive(
            sha256: other,
            manifest: testManifest('example.sandbox', version: '0.9.0'),
          ),
        ],
        sources: [
          ExactSourcePackage(
            sourceId: 'official',
            sourceName: 'Official',
            enabled: true,
            manifest: sandbox,
            downloadUrl: 'https://docs.topiaforge.dev/registry/sandbox.zip',
            sha256: testSha('example.sandbox'),
          ),
        ],
      );
      expect(
        catalog
            .resolvePackage(
              testPackage('example.sandbox'),
              developerMode: false,
              hostServesPackages: false,
            )
            .availability,
        JoinPackageAvailability.available,
      );
    });

    test('the exact archive installed still wins', () {
      final catalog = ExactPackageCatalog(
        installed: [
          ExactInstalledArchive(sha256: other, manifest: sandbox),
          ExactInstalledArchive(
            sha256: testSha('example.sandbox'),
            manifest: sandbox,
          ),
        ],
      );
      expect(
        catalog
            .resolvePackage(
              testPackage('example.sandbox'),
              developerMode: false,
              hostServesPackages: false,
            )
            .availability,
        JoinPackageAvailability.installed,
      );
    });
  });

  test('a server profile is a derived id that inherits nothing', () {
    final derived = plan.derivedProfile;
    expect(ServerProfiles.isServerProfile(derived), isTrue);
    expect(derived.id, JoinPlanner.derivedProfileId(hostAServerId));
    for (final profile in [
      derived.copyWith(inheritManagerModState: true),
      derived.copyWith(id: 'default'),
      derived.copyWith(id: 'mp-0123'),
      derived.copyWith(id: 'mp-0123456789ABCDEF'),
      derived.copyWith(id: 'mp-0123456789abcdef0'),
    ]) {
      expect(
        ServerProfiles.isServerProfile(profile),
        isFalse,
        reason: profile.id,
      );
    }
  });
}
