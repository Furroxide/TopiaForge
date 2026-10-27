import 'package:launcher_domain/launcher_domain.dart';
import 'package:test/test.dart';

import 'cli_join_fixture.dart';

void main() {
  late JoinFixture rig;
  setUp(() async {
    rig = JoinFixture();
    await rig.initialize();
  });
  tearDown(() => rig.dispose());

  void expectStoppedBeforeConsent() {
    expect(rig.launches, 0);
    expect(rig.steps, isNot(contains(JoinStage.downloading)));
    expect(rig.steps, isNot(contains(JoinStage.installing)));
    expect(rig.prompts, isNot(contains('Join [y/N]')));
    expect(rig.messages.last, startsWith('TFJOIN120:'));
  }

  test('disabled sources name every mod and how to recover', () async {
    rig.plan = await planJoin([
      for (final id in ['example.first', 'example.second'])
        (
          testPackage(id),
          JoinPackageResolution.unavailable(
            JoinPackageAvailability.inDisabledSource,
            sourceName: 'Community 203.0.113.99:27250',
          ),
        ),
    ]);

    expect(await rig.run(), 1);
    expectStoppedBeforeConsent();
    final lines = rig.messages.last.split('\n');
    expect(lines.first, contains('Fix what each mod below says'));
    expect(lines.skip(1), [
      for (final id in ['example.first', 'example.second'])
        'Mod $id: Only the disabled source Community [address] offers it. '
            'Enable it to join.',
    ]);
    expect(rig.messages.join('\n'), isNot(contains('203.0.113.99')));
  });

  test('admission refusal prints the session reason below the advice', () async {
    rig.plan = await planJoin(
      const [],
      protocolVersion: '0.9.0',
      runtimeVersion: '0.1.0',
    );
    expect(rig.plan.blockers.single.code, JoinBlockerCode.admissionBlocked);

    expect(await rig.run(), 1);
    expectStoppedBeforeConsent();
    expect(rig.messages.last.split('\n'), [
      startsWith('TFJOIN120: The server\'s rules would refuse'),
      'Mod (session): The server runs TopiaForge 0.1.0, '
          'whose multiplayer protocol this install cannot use.',
    ]);
  });

  test('admission mod identity and message both redact addresses', () async {
    final plan = rig.plan;
    rig.plan = JoinPlan(
      server: plan.server,
      packages: plan.packages,
      blockers: const [
        JoinPlanBlocker(
          JoinBlockerCode.admissionBlocked,
          'Update this mod; ask host.example:27250 or [2001:db8::1].',
          modId: 'example.mod 203.0.113.99',
        ),
      ],
      admission: plan.admission,
      capabilities: plan.capabilities,
      derivedProfile: plan.derivedProfile,
    );

    expect(await rig.run(), 1);
    expectStoppedBeforeConsent();
    expect(
      rig.messages.last.split('\n').last,
      'Mod example.mod [address]: Update this mod; ask [address] or [[address]].',
    );
    for (final private in ['203.0.113.99', 'host.example', '2001:db8::1']) {
      expect(rig.messages.join('\n'), isNot(contains(private)));
    }
  });

  for (final throws in [false, true]) {
    test('raw provider ${throws ? 'exceptions' : 'details'} stay withheld',
        () async {
      const private = r'provider-secret 203.0.113.99 C:\private\archive.zip';
      if (throws) {
        rig.beforeAnswer = (_) async => throw StateError(private);
      } else {
        rig
          ..failureStage = JoinStage.checking
          ..failure = JoinFailed(
            JoinFailureCode.admissionBlocked,
            detail: private,
          );
      }

      expect(await rig.run(), 1);
      expect(rig.launches, 0);
      for (final value in ['provider-secret', '203.0.113.99', r'C:\private']) {
        expect(rig.messages.join('\n'), isNot(contains(value)));
      }
    });
  }
}
