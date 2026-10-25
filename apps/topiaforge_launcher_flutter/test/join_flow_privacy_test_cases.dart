part of 'join_flow_test.dart';

/// The planted addresses as they may show: the IPv4 literal, the IPv6 one
/// (its brackets and port are separate tokens), and a name with a port.
const _plantedName = 'sams-pc.example:27250';
const _plantedTexts = [_planted, '2001:db8::77', _plantedName];

/// Every text the launcher produced while it ran: its events, changes and
/// errors as they print, the status bar, the error line and both logs.
final class _Said extends BlocObserver {
  final texts = <String>[];

  @override
  void onEvent(Bloc<dynamic, dynamic> bloc, Object? event) {
    super.onEvent(bloc, event);
    texts.add('$event');
  }

  @override
  void onChange(BlocBase<dynamic> bloc, Change<dynamic> change) {
    super.onChange(bloc, change);
    texts.add('$change');
    final state = change.nextState;
    if (state is LauncherState) {
      texts.addAll([
        state.statusMessage,
        state.errorMessage ?? '',
        state.launcherLog,
        state.recentLog,
        '${state.multiplayer}',
        '${state.multiplayer.join}',
        state.multiplayer.join.flow?.serverName ?? '',
      ]);
    }
  }

  @override
  void onError(BlocBase<dynamic> bloc, Object error, StackTrace stackTrace) {
    super.onError(bloc, error, stackTrace);
    texts.add('$error');
  }
}

/// Runs [body] with every bloc and every `debugPrint` recorded.
Future<void> _recording(Future<void> Function(_Said said) body) async {
  final said = _Said();
  final observer = Bloc.observer;
  final print = debugPrint;
  Bloc.observer = said;
  debugPrint = (message, {wrapWidth}) => said.texts.add(message ?? '');
  try {
    await body(said);
  } finally {
    Bloc.observer = observer;
    debugPrint = print;
  }
}

/// Every text on screen, offstage too, outside the direct-connect dialog:
/// what the widgets draw, what fields hold, tooltips and semantics labels.
List<String> _screenTexts(WidgetTester tester) {
  const typedIn = Key('direct-connect-dialog');
  bool inDialog(Element element) {
    var inside = element.widget.key == typedIn;
    if (!inside) {
      element.visitAncestorElements((ancestor) {
        inside = ancestor.widget.key == typedIn;
        return !inside;
      });
    }
    return inside;
  }

  Iterable<Element> all(Type type) => find
      .byType(type, skipOffstage: false)
      .evaluate()
      .where((element) => !inDialog(element));
  return [
    for (final element in all(RichText))
      (element.widget as RichText).text.toPlainText(),
    for (final element in all(EditableText))
      (element.widget as EditableText).controller.text,
    for (final element in all(Tooltip))
      (element.widget as Tooltip).message ?? '',
    for (final element in all(Semantics))
      (element.widget as Semantics).properties.label ?? '',
  ];
}

void _expectNoAddress(Iterable<String> texts, String where) {
  final seen = texts.toList();
  expect(seen, isNotEmpty, reason: where);
  for (final text in seen) {
    for (final planted in _plantedTexts) {
      expect(text, isNot(contains(planted)), reason: '$where: "$text"');
    }
  }
}

/// A host who writes addresses into everything the descriptor lets them.
Future<VerifiedServerDescriptor> _chattyServer() => _server(
  name: 'Sam at $_planted',
  gamemode: 'Sandbox on $_plantedEndpoint',
  world: 'World of $_plantedName',
);

/// Ways a join fails with addresses in the words that reach the launcher.
final _chattyFailures = <String, void Function(_JoinGateway gateway)>{
  'an exception while planning': (gateway) =>
      gateway.answers[JoinStage.checking] = (_) =>
          throw StateError('No route to $_planted.'),
  'a download that failed': (gateway) =>
      gateway.answers[JoinStage.downloading] = (_) => JoinFailed(
        JoinFailureCode.downloadFailed,
        detail: 'http://$_planted:8080/zombies did not answer.',
      ),
  'a refused start': (gateway) => gateway.launch = (_) => JoinLaunchRefused(
    JoinFailed(
      JoinFailureCode.launchBlocked,
      detail: 'Robotopia at $_plantedName is busy ($_plantedEndpoint).',
    ),
  ),
  'a start that threw': (gateway) =>
      gateway.launch = (_) =>
          throw FileSystemException('Cannot open', '//$_planted/share'),
};

void registerJoinPrivacyTests() {
  group('The join flow keeps addresses to itself', () {
    testWidgets('a typed address shows only in the dialog it was typed in, '
        'never in the flow, the status bar, the logs, the card or its copy', (
      tester,
    ) async {
      await _recording((said) async {
        _captureClipboard(tester);
        final server = (await tester.runAsync(_chattyServer))!;
        final gateway = _JoinGateway(server: server);
        final (_, repository) = await _openApp(tester, gateway: gateway);
        final bloc = _blocIn(tester);

        await tester.tap(find.byKey(const Key('join-by-address')));
        await tester.pumpAndSettle();
        await tester.enterText(
          find.byKey(const Key('direct-connect-host')),
          _planted,
        );
        await tester.enterText(
          find.byKey(const Key('direct-connect-port')),
          '27250',
        );
        await tester.tap(find.byKey(const Key('direct-connect-join')));
        await tester.pumpAndSettle();
        expect(find.byKey(const Key('join-question-direct')), findsOneWidget);
        _expectNoAddress(_screenTexts(tester), 'the direct prompt');

        await tester.tap(find.byKey(const Key('direct-connection-allow')));
        await tester.pumpAndSettle();
        expect(find.byKey(const Key('join-consent-sheet')), findsOneWidget);
        _expectNoAddress(_screenTexts(tester), 'the consent sheet');
        // The host's own words still show, without their addresses.
        expect(
          find.textContaining('Sam at $withheldAddress', skipOffstage: false),
          findsWidgets,
        );

        await tester.tap(find.byKey(const Key('join-consent-join')));
        await tester.pumpAndSettle();
        expect(_flowOf(bloc)!.launch, isNotNull);
        _expectNoAddress(_screenTexts(tester), 'the progress rail');
        repository.report(_activity(state: LaunchSessionState.connecting));
        await tester.pumpAndSettle();
        _expectNoAddress(_screenTexts(tester), 'connecting');

        repository.report(
          _activity(
            failure: LaunchMultiplayerFailure.timeout,
            detail: 'No answer from $_plantedEndpoint or $_plantedName.',
            error: LaunchOperationError(
              code: 'timedOut',
              message: 'The socket to $_planted closed.',
            ),
          ),
        );
        await tester.pumpAndSettle();
        expect(find.byKey(const Key('join-failure-card')), findsOneWidget);
        _expectNoAddress(_screenTexts(tester), 'the failure card');
        await tester.tap(find.byKey(const Key('join-copy-details')));
        await tester.pumpAndSettle();
        _expectNoAddress([_copied!], 'the copied details');

        // The game asks to join its server again: the launcher still knows
        // which one, and still shows nothing of where it is.
        gateway.handOff(
          JoinHandoffArrival(
            install: _install,
            handoff: SessionHandoff(
              requestId: _requestId,
              handoffId: 'handoff-1',
              target: const LaunchHandoffCurrent(),
            ),
          ),
        );
        await tester.pumpAndSettle();
        expect(find.byKey(const Key('join-question-direct')), findsOneWidget);
        _expectNoAddress(_screenTexts(tester), "the game's handoff");
        await tester.sendKeyEvent(LogicalKeyboardKey.escape);
        await tester.pumpAndSettle();
        expect(_flowOf(bloc), isNull);

        // Its own dialog shows it, in the history row.
        await tester.tap(find.byKey(const Key('join-by-address')));
        await tester.pumpAndSettle();
        expect(
          find.descendant(
            of: find.byKey(const Key('direct-connect-history-0')),
            matching: find.textContaining(_planted),
          ),
          findsOneWidget,
        );
        _expectNoAddress(_screenTexts(tester), 'behind the dialog');
        _expectNoAddress(said.texts, 'what the launcher said');
      });
    });

    for (final MapEntry(key: name, value: fail) in _chattyFailures.entries) {
      testWidgets('$name: the card, its copy and the logs hold no address', (
        tester,
      ) async {
        await _recording((said) async {
          _captureClipboard(tester);
          final server = (await tester.runAsync(_chattyServer))!;
          final gateway = _JoinGateway(server: server);
          fail(gateway);
          await _openApp(tester, gateway: gateway);
          _blocIn(
            tester,
          ).add(MultiplayerJoinRequested(JoinLinkTarget(_serverLink)));
          await tester.pumpAndSettle();
          final join = find.byKey(const Key('join-consent-join'));
          if (join.evaluate().isNotEmpty) {
            _expectNoAddress(_screenTexts(tester), 'the consent sheet');
            await tester.tap(join);
            await tester.pumpAndSettle();
          }
          expect(find.byKey(const Key('join-failure-card')), findsOneWidget);
          _expectNoAddress(_screenTexts(tester), 'the failure card');
          await tester.tap(find.byKey(const Key('join-copy-details')));
          await tester.pumpAndSettle();
          _expectNoAddress([_copied!], 'the copied details');
          _expectNoAddress(said.texts, 'what the launcher said');
        });
      });
    }

    testWidgets('settings that cannot be saved or read say so without the '
        "error's words", (tester) async {
      await _recording((said) async {
        final gateway = _JoinGateway(
          preferences: MultiplayerPreferences(settings: MultiplayerSettings()),
        )..failDisclosure = true;
        await _openApp(tester, gateway: gateway);
        _blocIn(
          tester,
        ).add(MultiplayerJoinRequested(JoinLinkTarget(_serverLink)));
        await tester.pumpAndSettle();
        await tester.tap(find.byKey(const Key('join-understand')));
        await tester.pumpAndSettle();
        expect(find.byKey(const Key('join-problem')), findsOneWidget);
        _expectNoAddress(_screenTexts(tester), 'a disclosure not saved');

        await tester.tap(find.byKey(const Key('join-close')));
        await tester.pumpAndSettle();
        gateway.failure = StateError('Settings at //$_planted/share are gone.');
        _blocIn(
          tester,
        ).add(MultiplayerJoinRequested(JoinLinkTarget(_serverLink)));
        await tester.pumpAndSettle();
        expect(find.byKey(const Key('join-problem')), findsOneWidget);
        _expectNoAddress(_screenTexts(tester), 'unreadable settings');
        _expectNoAddress(said.texts, 'what the launcher said');
      });
    });
  });
}
