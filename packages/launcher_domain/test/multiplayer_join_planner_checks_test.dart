import 'package:launcher_domain/launcher_domain.dart';
import 'package:test/test.dart';

import 'multiplayer_join_support.dart';

void main() {
  group('host-served packages', () {
    final hostOffered = JoinPackageResolution.hostOffered(sizeBytes: 4096);

    test('block the join outside developer mode', () async {
      final result = await planJoin([
        (testPackage('example.local'), hostOffered),
      ], hostServesPackages: true);
      expect(result.blockers.single.code, JoinBlockerCode.contentUnavailable);
      expect(result.blockers.single.message, contains('developer mode'));
      expect(result.hasUnverifiedPackages, isFalse);
    });

    test('block in developer mode when the host does not serve them', () async {
      final result = await planJoin([
        (testPackage('example.local'), hostOffered),
      ], environment: environmentWith(developerMode: true));
      expect(result.blockers.single.message, contains('does not serve'));
    });

    test('are flagged in developer mode when the host serves them', () async {
      final result = await planJoin(
        [
          (testPackage('example.local'), hostOffered),
          (
            testPackage(
              'example.extra',
              role: DescriptorPackageRole.sessionOptional,
            ),
            hostOffered,
          ),
        ],
        environment: environmentWith(developerMode: true),
        hostServesPackages: true,
      );
      expect(result.isJoinable, isTrue);
      expect(
        packageIn(result, 'example.local').disposition,
        JoinPackageDisposition.downloadFromHost,
      );
      expect(
        packageIn(result, 'example.extra').disposition,
        JoinPackageDisposition.skip,
      );
      expect(result.hasUnverifiedPackages, isTrue);
      expect(result.needsDownloads, isTrue);
      expect(result.installActions, isEmpty);
      expect(result.hostServedPackages.single.id, 'example.local');
      expect(result.capabilities, isEmpty);
      expect(result.derivedProfile.selectedVersions, {
        'example.local': '1.0.0',
      });
    });
  });

  group('session-wide checks', () {
    test('another game build blocks; spellings of one build agree', () async {
      final mismatch = await planJoin(
        const [],
        environment: environmentWith(gameBuild: '2478'),
      );
      expect(mismatch.blockers.single.code, JoinBlockerCode.gameBuildMismatch);
      expect(mismatch.blockers.single.message, contains('2545'));
      final sameBuild = await planJoin(
        const [],
        environment: environmentWith(gameBuild: '0.0.2545'),
      );
      expect(sameBuild.isJoinable, isTrue);
    });

    test('an incompatible protocol reads as an outdated runtime when the '
        'host is newer', () async {
      final newer = await planJoin(
        const [],
        protocolVersion: '2.0.0',
        runtimeVersion: '0.3.0',
      );
      expect(newer.blockers.single.code, JoinBlockerCode.runtimeOutdated);
      expect(newer.blockers.single.message, contains('0.3.0'));
      final older = await planJoin(
        const [],
        protocolVersion: '0.9.0',
        runtimeVersion: '0.1.0',
      );
      expect(older.blockers.single.code, JoinBlockerCode.admissionBlocked);
    });

    test('packages the installed runtime cannot load block once', () async {
      final result = await planJoin([
        (
          testPackage('example.future'),
          availableOf(testManifest('example.future', loaderRange: '>=0.3.0')),
        ),
        (
          testPackage('example.past'),
          availableOf(testManifest('example.past', loaderRange: '<0.2.0')),
        ),
        (
          testPackage('example.linux'),
          availableOf(testManifest('example.linux', platforms: ['linux'])),
        ),
        (
          testPackage(
            'example.optional',
            role: DescriptorPackageRole.sessionOptional,
          ),
          installedOf(
            testManifest(
              'example.optional',
              mode: 'session',
              presence: 'optional',
              loaderRange: '>=0.3.0',
            ),
          ),
        ),
      ]);
      final codes = {
        for (final blocker in result.blockers) blocker.modId: blocker.code,
      };
      expect(codes, {
        'example.future': JoinBlockerCode.runtimeOutdated,
        'example.past': JoinBlockerCode.contentUnavailable,
        'example.linux': JoinBlockerCode.contentUnavailable,
      });
      expect(
        result.blockers,
        hasLength(3),
        reason: 'admission adds no repeats',
      );
      expect(
        packageIn(result, 'example.optional').disposition,
        JoinPackageDisposition.skip,
      );
    });

    test('blockers sort by code, then mod', () async {
      final result = await planJoin([
        (testPackage('example.b'), null),
        (testPackage('example.a'), null),
        (
          testPackage('example.solo'),
          installedOf(testManifest('example.solo', mode: 'standalone')),
        ),
      ], environment: environmentWith(gameBuild: '2478'));
      expect(result.blockers.map((item) => (item.code, item.modId)), [
        (JoinBlockerCode.gameBuildMismatch, ''),
        (JoinBlockerCode.standaloneOnServer, 'example.solo'),
        (JoinBlockerCode.contentUnavailable, 'example.a'),
        (JoinBlockerCode.contentUnavailable, 'example.b'),
      ]);
    });

    test(
      'a server without packages is joinable with an empty profile',
      () async {
        final result = await planJoin(const []);
        expect(result.isJoinable, isTrue);
        expect(result.needsDownloads, isFalse);
        expect(result.derivedProfile.enabledMods, isEmpty);
        expect(result.capabilities, isEmpty);
      },
    );

    test('an environment without a build, runtime or protocol is refused', () {
      expect(() => environmentWith(gameBuild: ' '), throwsArgumentError);
      expect(() => environmentWith(runtimeVersion: ''), throwsArgumentError);
      expect(() => environmentWith(protocolVersion: ''), throwsArgumentError);
    });
  });

  group('consent key', () {
    final sandbox = testManifest('example.sandbox', capabilities: ['physics']);

    test(
      'is stable for the same plan and changes with what is consented to',
      () async {
        final base = await planJoin([
          (testPackage('example.sandbox'), availableOf(sandbox)),
        ]);
        final installed = await planJoin([
          (testPackage('example.sandbox'), installedOf(sandbox)),
        ]);
        expect(
          installed.consentKey,
          base.consentKey,
          reason: 'where a package comes from is not consent',
        );
        final voiceOff = await planJoin([
          (testPackage('example.sandbox'), availableOf(sandbox)),
        ], voice: false);
        expect(voiceOff.consentKey, isNot(base.consentKey));
        final newer = await planJoin([
          (
            testPackage('example.sandbox', version: '1.1.0'),
            availableOf(
              testManifest(
                'example.sandbox',
                version: '1.1.0',
                capabilities: ['physics'],
              ),
            ),
          ),
        ]);
        expect(newer.consentKey, isNot(base.consentKey));
      },
    );

    test('survives any capability text a manifest holds', () async {
      final odd = testManifest('example.odd', capabilities: ['\ud800odd']);
      final result = await planJoin([
        (testPackage('example.odd'), availableOf(odd)),
      ]);
      expect(result.consentKey, contains('example.odd'));
    });
  });

  group('admission findings', () {
    // A session manifest without its contract lock is invalid for admission.
    ModManifest unlocked(String id, {String loaderRange = '*'}) => testManifest(
      id,
      mode: 'session',
      withLock: false,
      loaderRange: loaderRange,
    );
    DescriptorPackage session(String id) =>
        testPackage(id, role: DescriptorPackageRole.sessionRequired);

    test(
      'a package invalid on both sides is reported once, without a side',
      () async {
        final result = await planJoin([
          (
            session('example.unlocked'),
            availableOf(unlocked('example.unlocked')),
          ),
        ]);
        expect(
          result.admission.mismatches.where(
            (item) => item.modId == 'example.unlocked',
          ),
          hasLength(2),
          reason: 'admission reports the server and the client copy',
        );
        final blocker = result.blockers.single;
        expect(blocker.code, JoinBlockerCode.admissionBlocked);
        expect(blocker.modId, 'example.unlocked');
        expect(blocker.message, startsWith('The manifest is invalid'));
        expect(blocker.message, isNot(contains('client')));
      },
    );

    test('a package with its own blocker gains no admission repeats', () async {
      final result = await planJoin([
        (
          session('example.unlocked'),
          availableOf(unlocked('example.unlocked', loaderRange: '>=0.3.0')),
        ),
      ]);
      expect(result.admission.isAdmitted, isFalse);
      expect(result.blockers.single.code, JoinBlockerCode.runtimeOutdated);
    });
  });
}
