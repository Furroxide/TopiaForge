// Exercise the application's pure Dart mapper with the production domain and data
// tests. These two libraries currently live in the Flutter application.
// ignore_for_file: avoid_relative_lib_imports

import 'package:launcher_domain/launcher_domain.dart';
import 'package:test/test.dart';

import '../../../apps/topiaforge_launcher_flutter/lib/src/join_activity.dart';
import '../../../apps/topiaforge_launcher_flutter/lib/src/join_texts.dart';
import '../../launcher_domain/test/multiplayer_fixtures.dart';
import '../../launcher_domain/test/multiplayer_join_support.dart';

void main() {
  late JoinSessionState launching;
  setUpAll(() async {
    final plan = await planJoin([]);
    launching =
        [
          JoinTargetResolved(hostAServerId),
          JoinDescriptorVerified(plan.server),
          JoinPlanReady(plan),
          const JoinConsentGiven(),
          const JoinProfilePrepared(),
        ].fold(
          startJoin(JoinLinkTarget(SessionLink.server(hostAServerId))),
          JoinSession.reduce,
        );
    expect(launching.stage, JoinStage.launching);
  });

  LaunchActivity activity(LaunchOutcome outcome) => LaunchActivity(
    requestId: outcome.requestId,
    profileId: 'profile',
    profileRevision: 0,
    installIdentity: 'fixture',
    packageDigest: '0123456789abcdef',
    command: 'join-session',
    launchOutcome: outcome.kind == 'launch' ? outcome : null,
    sessionOutcome: outcome.kind == 'session' ? outcome : null,
  );
  JoinSessionState apply(JoinSessionState state, LaunchActivity value) =>
      joinEventsFor(state, value).fold(state, JoinSession.reduce);
  final fixtures = asObjects(
    readMultiplayerFixture('progress-v2/forward-compat.json')['valid'],
  );
  LaunchOutcome read(String id) => LaunchTransport.readOutcome(
    fixtures.singleWhere((item) => item['id'] == id)['text']! as String,
  );

  for (final stage in [
    JoinStage.launching,
    JoinStage.connecting,
    JoinStage.synchronizing,
  ]) {
    test('unknown failure settles $stage with fixed advice and Retry', () {
      var state = launching;
      if (stage != JoinStage.launching) {
        state = JoinSession.reduce(state, const JoinGameStarted());
      }
      if (stage == JoinStage.synchronizing) {
        state = JoinSession.reduce(state, const JoinConnected());
      }
      final result = apply(state, activity(read('join-receiving')));
      expect(result.stage, JoinStage.ended);
      final failure = result.outcome!.failure!;
      expect(failure.code, JoinFailureCode.other);
      expect(
        failure.stage,
        stage == JoinStage.launching ? JoinStage.connecting : stage,
      );
      final text = JoinFailureText.of(failure);
      expect(text.title, 'The join could not finish');
      expect(text.bodyFor(failure), LaunchOutcomeMultiplayer.otherDetail);
      expect(text.retry, isTrue);
      expect(text.action, JoinFailureAction.none);
      expect(result.replans, 0);
    });
  }
  test('unknown end settles a joined session with fixed recovery advice', () {
    final joined = [
      const JoinGameStarted(),
      const JoinConnected(),
      const JoinSynchronized(),
    ].fold(launching, JoinSession.reduce);
    final ended = apply(joined, activity(read('join-end-world')));
    expect(ended.stage, JoinStage.ended);
    expect(ended.outcome!.sessionEnd, JoinSessionEndReason.other);
    expect(
      joinStatusOf(ended, 'Sam'),
      'The session ended. Try again; if it keeps failing, update TopiaForge.',
    );
  });
  test('a generic failure without detail still offers fixed advice', () {
    final connecting = JoinSession.reduce(launching, const JoinGameStarted());
    final ended = JoinSession.reduce(
      connecting,
      JoinFailed(JoinFailureCode.other),
    );
    final failure = ended.outcome!.failure!;
    expect(
      JoinFailureText.of(failure).bodyFor(failure),
      LaunchOutcomeMultiplayer.otherDetail,
    );
  });
  test(
    'unknown results cannot reopen cancellation, including deferred cancel',
    () {
      for (final started in [false, true]) {
        final state = started
            ? JoinSession.reduce(launching, const JoinGameStarted())
            : launching;
        final cancelled = JoinSession.reduce(
          state,
          const JoinCancelRequested(),
        );
        final ended = apply(cancelled, activity(read('join-failure')));
        expect(ended.outcome!.wasCancelled, isTrue);
        expect(apply(ended, activity(read('join-end'))), same(ended));
      }
    },
  );
  test('known follow failure retains its specific stage and advice', () {
    final json = LaunchTransport.writeOutcome(
      read('join-failure'),
    ).replaceAll('"other"', '"world-follow-failed"');
    final ended = apply(launching, activity(LaunchTransport.readOutcome(json)));
    expect(ended.outcome!.failure!.code, JoinFailureCode.worldFollowFailed);
    expect(ended.outcome!.failure!.stage, JoinStage.synchronizing);
  });
}
