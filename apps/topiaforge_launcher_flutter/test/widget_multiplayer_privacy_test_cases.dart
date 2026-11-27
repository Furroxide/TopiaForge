part of 'widget_test.dart';

/// What the launcher said while a test drove it: every event and change the
/// bloc observer sees, and each state's status line, error line, problems
/// and launcher log.
final class _RecordingObserver extends BlocObserver {
  final said = <String>[];

  @override
  void onEvent(Bloc<dynamic, dynamic> bloc, Object? event) {
    super.onEvent(bloc, event);
    said.add('$event');
  }

  @override
  void onChange(BlocBase<dynamic> bloc, Change<dynamic> change) {
    super.onChange(bloc, change);
    said.add('$change');
    final next = change.nextState;
    if (next is LauncherState) {
      said.addAll([
        next.statusMessage,
        next.errorMessage ?? '',
        next.launcherLog,
        next.multiplayer.settings.problem ?? '',
        next.multiplayer.network.problem ?? '',
        '${next.multiplayer.settings}',
        '${next.multiplayer.network}',
        '${next.multiplayer.settings.preferences}',
        '${next.multiplayer.network.report?.toJson()}',
        '${next.multiplayer.network.lan?.toJson()}',
      ]);
    }
  }

  @override
  void onError(BlocBase<dynamic> bloc, Object error, StackTrace stackTrace) {
    super.onError(bloc, error, stackTrace);
    said.add('$error');
  }
}

/// Every text on screen, text fields included.
Iterable<String> _screenText(WidgetTester tester) sync* {
  for (final text in tester.widgetList<Text>(find.byType(Text))) {
    yield text.data ?? text.textSpan?.toPlainText() ?? '';
  }
  for (final field in tester.widgetList<EditableText>(
    find.byType(EditableText),
  )) {
    yield field.controller.text;
  }
}

void registerMultiplayerPrivacyTests() {
  testWidgets('no address in any failure reaches the screen, the status bar, '
      'the log, a print or toString', (tester) async {
    final observer = _RecordingObserver();
    final previousObserver = Bloc.observer;
    final previousPrint = debugPrint;
    final printed = <String>[];
    Bloc.observer = observer;
    debugPrint = (message, {wrapWidth}) => printed.add(message ?? '');
    try {
      await _driveEveryMultiplayerState(tester, observer, printed);
    } finally {
      // The binding checks that its debug hooks are back before the test
      // ends.
      Bloc.observer = previousObserver;
      debugPrint = previousPrint;
    }
  });
}

/// Drives every state of Settings → Multiplayer and Diagnostics → Network
/// through failures whose own text names an address, and scans what was
/// shown, said and printed.
Future<void> _driveEveryMultiplayerState(
  WidgetTester tester,
  _RecordingObserver observer,
  List<String> printed,
) async {
  final shown = <String>[];
  void look() => shown.addAll(_screenText(tester));
  Future<void> press(Finder finder) async {
    await tester.ensureVisible(finder);
    await tester.tap(finder);
    await tester.pumpAndSettle();
    look();
  }

  final gateway = _FakeMultiplayerGateway()..unreadable = true;
  await _openMultiplayerSettings(
    tester,
    gateway,
    links: _FakeSessionLinkHandling(),
  );
  look();

  // Every state of Settings → Multiplayer, each reached through a failure
  // whose own text names an address.
  gateway.unreadable = false;
  await press(find.text('Try again'));
  gateway.failSaves = true;
  await press(find.byKey(const Key('multiplayer-upnp')));
  gateway
    ..failSaves = false
    ..running = GameRunState.running;
  await press(find.byKey(const Key('multiplayer-links')));
  // A change asks about the game first, and finds it running.
  await press(find.byKey(const Key('multiplayer-upnp')));
  expect(find.byKey(const Key('multiplayer-lock')), findsOneWidget);
  gateway.running = GameRunState.unknown;
  await press(find.text('Check again'));
  gateway
    ..running = GameRunState.notRunning
    ..round = (current) => MultiplayerPreferencesRoundFailed(
      current,
      MultiplayerRoundProblem.preferencesNotSaved,
    );
  await press(find.text('Check again'));
  gateway
    ..round = null
    ..unreadable = true;
  await press(find.byKey(const Key('multiplayer-lan-discovery')));
  expect(find.byKey(const Key('multiplayer-unreadable')), findsOneWidget);

  // Every state of Diagnostics → Network.
  gateway
    ..unreadable = false
    ..lan = LanBrowseSnapshot(
      status: LanDiscoveryStatus.unavailable,
      problem: LanDiscoveryProblem.permissionDenied,
    );
  final bloc = BlocProvider.of<LauncherBloc>(
    tester.element(find.byType(LauncherShell)),
  );
  bloc.add(const LauncherSectionSelected(LauncherSection.diagnostics));
  await tester.pumpAndSettle();
  await press(find.widgetWithText(Tab, 'Network'));
  gateway.failure = SocketException(
    'Connection refused by $_plantedEndpoint',
    address: InternetAddress(_plantedAddress),
    port: 27250,
  );
  await press(find.byKey(const Key('network-refresh')));
  await press(find.byKey(const Key('network-check-ports')));
  await press(find.text('Continue'));
  await press(find.byKey(const Key('network-listen')));
  gateway.failure = null;
  await press(find.byKey(const Key('network-check-ports')));
  expect(find.byKey(const Key('network-problem')), findsNothing);
  await press(find.text('Home').first);

  // The failures did carry the address, so the scan below means something.
  expect(gateway.calls, contains('check ports'));
  expect(
    '${SocketException('x', address: InternetAddress(_plantedAddress))}',
    contains(_plantedAddress),
  );
  expect(shown, isNotEmpty);
  expect(observer.said, isNotEmpty);
  for (final (source, lines) in [
    ('the screen', shown),
    ('the bloc', observer.said),
    ('prints', printed),
  ]) {
    for (final line in lines) {
      expect(line, isNot(contains(_plantedAddress)), reason: source);
      expect(line, isNot(contains('2001:db8')), reason: source);
    }
  }
}
