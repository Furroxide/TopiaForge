part of 'join_flow_test.dart';

void registerAddressSurfaceTests() {
  for (final (text, address) in [
    ('peer:2001:db8::77', '2001:db8::77'),
    ('peer:2001:db8::', '2001:db8::'),
    ('host:fe80::1%4', 'fe80::1'),
    ('host:[2001:db8::77]:27250', '2001:db8::77'),
    ('peer:::ffff:203.0.113.77', '203.0.113.77'),
    ('peer:::ffff:cb00:714d', '::ffff:cb00:714d'),
    ('peer:64:ff9b::cb00:714d', '64:ff9b::cb00:714d'),
    ('peer.203.0.113.77.example.net', '203.0.113.77'),
    ('peer:2001:db8::77.example.net', '2001:db8::77'),
    ('dead2001:db8::77', '2001:db8::77'),
    ('peer.203.0.113.77.1', '203.0.113.77'),
  ]) {
    testWidgets('host text $text is withheld on every join surface', (
      tester,
    ) async {
      await _recording((said) async {
        _captureClipboard(tester);
        final gateway = await _toHostServedSheet(tester, serverName: text);
        void hidden(Iterable<String> texts) {
          for (final value in texts) {
            expect(value, isNot(contains(address)));
          }
        }

        hidden(_screenTexts(tester));
        gateway.answers[JoinStage.downloading] = (_) =>
            JoinFailed(JoinFailureCode.downloadFailed, detail: text);
        await _tapHostServedBox(tester);
        await tester.tap(find.byKey(const Key('join-consent-join')));
        await tester.pumpAndSettle();
        expect(find.byKey(const Key('join-failure-card')), findsOneWidget);
        hidden(_screenTexts(tester));
        await tester.tap(find.byKey(const Key('join-copy-details')));
        await tester.pumpAndSettle();
        hidden([_copied!]);
        hidden(said.texts);
      });
    });
  }

  testWidgets(
    'host-chosen id and semantic version with numeric suffixes are withheld',
    (tester) async {
      await _recording((said) async {
        _captureClipboard(tester);
        await _toHostServedSheet(
          tester,
          hostedId: 'peer.$_planted.1',
          hostedVersion: '1.0.0-peer.$_planted.1',
        );
        _expectNoAddress(_screenTexts(tester), 'host id and version');
        final bloc = _blocIn(tester);
        bloc.add(MultiplayerJoinCancelled(_flowOf(bloc)!.id));
        await tester.pumpAndSettle();
        bloc.add(MultiplayerJoinRequested(JoinLinkTarget(_serverLink)));
        await tester.pumpAndSettle();
        expect(find.byKey(const Key('join-failure-card')), findsOneWidget);
        _expectNoAddress(_screenTexts(tester), 'blockers');
        await tester.tap(find.byKey(const Key('join-copy-details')));
        await tester.pumpAndSettle();
        _expectNoAddress([_copied!], 'copied blockers');
        _expectNoAddress(said.texts, 'bloc and log output');
      });
    },
  );
}
