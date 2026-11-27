import 'dart:io';
import 'package:launcher_domain/launcher_domain.dart';
import 'package:test/test.dart';
import 'package:topiaforge/src/cli_join_consent.dart';
import 'cli_join_fixture.dart';

void main() {
  test('privacy, voice and host warning use the approved words', () async {
    final plan = await planJoin(
      [(testPackage('example.local'), JoinPackageResolution.hostOffered())],
      environment: environmentWith(developerMode: true),
      hostServesPackages: true,
    );
    final approved = File('../../docs/internal/MultiplayerPrivacyText.md')
        .readAsStringSync()
        .replaceAll(RegExp(r'\r?\n> ?'), ' ')
        .replaceAll(RegExp(r'\s+'), ' ');
    for (final direct in [false, true]) {
      for (final voice in [false, true]) {
        var state = JoinSession.start(
          JoinAddressTarget(host: 'localhost', port: 27250),
          privacy: MultiplayerSettings()
              .acknowledgePrivacyDisclosure()
              .privacyForSession(allowDirect: direct),
        );
        state = JoinSession.reduce(state, JoinDescriptorVerified(plan.server));
        state = JoinSession.reduce(state, JoinPlanReady(plan));
        final lines = cliJoinConsent(
          state,
          voice: voice,
          profileIds: {plan.derivedProfile.id},
          running: JoinGameRunning.owned,
        ).toList();
        for (final line in lines.where(
          (line) =>
              line.startsWith('Privacy:') ||
              line.startsWith('Direct connection:') ||
              line.startsWith('Voice chat:'),
        )) {
          expect(approved, contains(line));
        }
        expect(
          lines,
          containsAll([
            JoinHostServedText.warning,
            JoinHostServedText.checkbox,
            JoinConsentText.unknownCapabilities,
            JoinConsentText.warning,
            JoinConsentText.ownedGame,
          ]),
        );
        expect(approved, contains(JoinHostServedText.warning));
        expect(lines.any((line) => line.startsWith('Updates profile')), isTrue);
      }
    }
  });
}
