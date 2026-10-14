import 'package:launcher_domain/launcher_domain.dart';
import 'package:test/test.dart';

import 'multiplayer_join_support.dart';

void main() {
  group('derived profile', () {
    test('ids and names follow the plan', () {
      expect(
        JoinPlanner.derivedProfileId(hostAServerId),
        'mp-c9075fd68f79b153',
      );
      expect(
        JoinPlanner.derivedProfileId(
          'cf82f7bc6b072f456554dd30c31b12fe5af423bbe03ff908124a1a08f0f16443',
        ),
        'mp-e51efbed9a31727c',
      );
      expect(() => JoinPlanner.derivedProfileId('A' * 64), throwsArgumentError);
      expect(JoinPlanner.derivedProfileName('LAN'), 'Server · LAN');
    });

    test('pins every enabled package and inherits nothing', () async {
      final sandbox = testManifest(
        'example.sandbox',
        capabilities: ['physics'],
      );
      final zombies = testManifest(
        'example.zombies',
        mode: 'session',
        capabilities: ['hud', 'physics'],
      );
      final result = await planJoin([
        (testPackage('example.sandbox'), installedOf(sandbox)),
        (
          testPackage(
            'example.zombies',
            role: DescriptorPackageRole.sessionRequired,
          ),
          availableOf(zombies),
        ),
      ]);
      expect(result.isJoinable, isTrue);
      expect(result.blockers, isEmpty);
      final profile = result.derivedProfile;
      expect(profile.id, 'mp-c9075fd68f79b153');
      expect(profile.name, "Server · Sam's Sandbox");
      expect(profile.inheritManagerModState, isFalse);
      expect(profile.enabledMods, {'example.sandbox', 'example.zombies'});
      expect(profile.selectedVersions, {
        'example.sandbox': '1.0.0',
        'example.zombies': '1.0.0',
      });
      expect(result.capabilities, ['hud', 'physics']);
      expect(result.admission.isAdmitted, isTrue);
      expect(result.admission.activeSessionMods, [
        'example.sandbox',
        'example.zombies',
      ]);
    });
  });

  group('package decisions', () {
    test('installed archives are used and the rest downloaded', () async {
      final installed = testManifest('example.installed');
      final remote = testManifest('example.remote');
      final result = await planJoin([
        (testPackage('example.installed'), installedOf(installed)),
        (testPackage('example.remote'), availableOf(remote, size: 2048)),
      ]);
      expect(
        packageIn(result, 'example.installed').disposition,
        JoinPackageDisposition.useInstalled,
      );
      expect(
        packageIn(result, 'example.remote').disposition,
        JoinPackageDisposition.download,
      );
      expect(result.needsDownloads, isTrue);
      expect(result.hasUnverifiedPackages, isFalse);
      final action = result.installActions.single;
      expect(action.modId, 'example.remote');
      expect(action.version, '1.0.0');
      expect(action.packageSha256, testSha('example.remote'));
      expect(action.expectedManifest, same(remote));
      expect(action.packageUrl, endsWith('example.remote.zip'));
      expect(action.sourceName, 'TopiaForge Mod Registry');
    });

    test('trusted optional packages are used only when installed', () async {
      final kept = testManifest(
        'example.kept',
        mode: 'session',
        presence: 'optional',
        capabilities: ['hud'],
      );
      final offered = testManifest(
        'example.offered',
        mode: 'session',
        presence: 'optional',
        capabilities: ['microphone'],
      );
      DescriptorPackage optional(String id) =>
          testPackage(id, role: DescriptorPackageRole.sessionOptional);
      final result = await planJoin([
        (optional('example.kept'), installedOf(kept)),
        (optional('example.offered'), availableOf(offered)),
      ]);
      expect(result.isJoinable, isTrue);
      expect(
        packageIn(result, 'example.kept').disposition,
        JoinPackageDisposition.useInstalled,
      );
      expect(
        packageIn(result, 'example.offered').disposition,
        JoinPackageDisposition.skip,
      );
      expect(result.needsDownloads, isFalse);
      expect(result.capabilities, ['hud']);
      expect(result.derivedProfile.enabledMods, {'example.kept'});
      expect(
        result.admission.inactiveSessionReasons.map((item) => item.modId),
        ['example.offered'],
      );
    });

    test('no trusted manifest blocks whatever role the host claims', () async {
      for (final role in DescriptorPackageRole.values) {
        final result = await planJoin([
          (testPackage('example.hostonly', role: role), null),
        ]);
        expect(result.isJoinable, isFalse, reason: role.wireName);
        expect(ServerReadiness.of(result), ServerReadiness.blocked);
        final blocker = result.blockers.single;
        expect(blocker.code, JoinBlockerCode.contentUnavailable);
        expect(blocker.modId, 'example.hostonly');
        expect(
          packageIn(result, 'example.hostonly').disposition,
          JoinPackageDisposition.blocked,
        );
        expect(result.derivedProfile.enabledMods, isEmpty);
        expect(
          blocker.message.contains('optional'),
          role == DescriptorPackageRole.sessionOptional,
          reason: 'the claim shapes only the message',
        );
      }
    });

    test('a required package without a trusted copy blocks, per mod', () async {
      final cases = {
        'example.disabled': JoinPackageResolution.unavailable(
          JoinPackageAvailability.inDisabledSource,
          sourceName: 'Community',
        ),
        'example.mismatch': JoinPackageResolution.unavailable(
          JoinPackageAvailability.hashMismatch,
        ),
        'example.version': JoinPackageResolution.unavailable(
          JoinPackageAvailability.versionUnavailable,
        ),
        'example.missing': null,
      };
      final result = await planJoin([
        for (final entry in cases.entries)
          (testPackage(entry.key), entry.value),
      ]);
      expect(result.isJoinable, isFalse);
      expect(result.blockers.map((item) => item.modId), [
        'example.disabled',
        'example.mismatch',
        'example.missing',
        'example.version',
      ]);
      expect(result.blockers.map((item) => item.code).toSet(), {
        JoinBlockerCode.contentUnavailable,
      });
      expect(result.blockers.first.message, contains('Community'));
      expect(result.blockers[2].message, contains('None of your'));
      for (final id in cases.keys) {
        expect(
          packageIn(result, id).disposition,
          JoinPackageDisposition.blocked,
        );
      }
      expect(result.derivedProfile.enabledMods, isEmpty);
    });

    test('trusted manifests overrule the roles the host claims', () async {
      final shared = testManifest('example.shared');
      final optional = testManifest(
        'example.optional',
        mode: 'session',
        presence: 'optional',
      );
      final result = await planJoin([
        (
          testPackage(
            'example.shared',
            role: DescriptorPackageRole.sessionOptional,
          ),
          availableOf(shared),
        ),
        (testPackage('example.optional'), availableOf(optional)),
      ]);
      final sharedPlan = packageIn(result, 'example.shared');
      expect(sharedPlan.disposition, JoinPackageDisposition.download);
      expect(
        sharedPlan.trustedMode,
        ModMultiplayerEffectiveMode.implicitShared,
      );
      expect(sharedPlan.note, contains('session-optional'));
      expect(sharedPlan.note, contains('implicit'));
      final optionalPlan = packageIn(result, 'example.optional');
      expect(optionalPlan.disposition, JoinPackageDisposition.skip);
      expect(optionalPlan.note, contains('session-optional'));
    });

    test(
      'an archive whose trusted manifest is another package blocks',
      () async {
        final other = testManifest('example.other', version: '2.0.0');
        final result = await planJoin([
          (testPackage('example.claimed'), installedOf(other)),
        ]);
        expect(result.blockers.single.code, JoinBlockerCode.contentUnavailable);
        expect(result.blockers.single.modId, 'example.claimed');
        expect(result.blockers.single.message, contains('example.other@2.0.0'));
        expect(
          packageIn(result, 'example.claimed').disposition,
          JoinPackageDisposition.blocked,
        );
      },
    );

    test('host-side packages are skipped, standalone ones block', () async {
      final result = await planJoin([
        (
          testPackage('example.hud'),
          installedOf(testManifest('example.hud', mode: 'client-local')),
        ),
        (
          testPackage('example.admin'),
          availableOf(testManifest('example.admin', mode: 'server-only')),
        ),
        (
          testPackage('example.solo'),
          installedOf(testManifest('example.solo', mode: 'standalone')),
        ),
      ]);
      expect(
        packageIn(result, 'example.hud').disposition,
        JoinPackageDisposition.skip,
      );
      expect(
        packageIn(result, 'example.admin').disposition,
        JoinPackageDisposition.skip,
      );
      expect(
        packageIn(result, 'example.solo').disposition,
        JoinPackageDisposition.blocked,
      );
      expect(result.blockers.single.code, JoinBlockerCode.standaloneOnServer);
      expect(result.blockers.single.modId, 'example.solo');
      expect(result.derivedProfile.enabledMods, isEmpty);
    });

    test('an invalid trusted manifest blocks through admission', () async {
      final result = await planJoin([
        (
          testPackage('example.bogus'),
          installedOf(testManifest('example.bogus', mode: 'bogus')),
        ),
      ]);
      expect(result.blockers.single.code, JoinBlockerCode.admissionBlocked);
      expect(result.blockers.single.modId, 'example.bogus');
      expect(
        packageIn(result, 'example.bogus').disposition,
        JoinPackageDisposition.blocked,
      );
    });
  });
}
