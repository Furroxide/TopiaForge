part of 'widget_test.dart';

/// Opens Diagnostics → Network in the launcher over [gateway].
Future<LauncherBloc> _openNetwork(
  WidgetTester tester,
  _FakeMultiplayerGateway gateway, {
  Size size = const Size(1280, 900),
  bool settle = true,
}) async {
  tester.view.physicalSize = size;
  tester.view.devicePixelRatio = 1;
  addTearDown(tester.view.reset);
  await tester.pumpWidget(
    TopiaForgeLauncherApp(
      repository: _FakeLauncherRepository(snapshot: _readySnapshot()),
      multiplayer: gateway,
    ),
  );
  await tester.pumpAndSettle();
  final bloc = BlocProvider.of<LauncherBloc>(
    tester.element(find.byType(LauncherShell)),
  );
  bloc.add(const LauncherSectionSelected(LauncherSection.diagnostics));
  await tester.pumpAndSettle();
  await tester.tap(find.widgetWithText(Tab, 'Network'));
  if (settle) {
    await tester.pumpAndSettle();
  } else {
    // A check still running shows a progress bar that never settles.
    for (var frame = 0; frame < 10; frame++) {
      await tester.pump(const Duration(milliseconds: 100));
    }
  }
  return bloc;
}

void registerMultiplayerNetworkWidgetTests() {
  MultiplayerNetworkView view(LauncherBloc bloc) =>
      bloc.state.multiplayer.network;

  group('Diagnostics → Network, in the bloc', () {
    test('a check runs beside the lane, the newest wins, and leaving drops '
        'it', () async {
      final gateway = _FakeMultiplayerGateway()..hold = true;
      final bloc = _multiplayerBloc(gateway, start: false);
      bloc.add(const MultiplayerNetworkCheckRequested());
      await _idle();
      expect(view(bloc).checking, isTrue);
      bloc.add(const MultiplayerNetworkCheckRequested());
      await _idle();
      expect(gateway.checkTokens.first!.isCancelled, isTrue);
      gateway.heldChecks[1].complete(
        _networkReport(service: MultiplayerServiceStatus.configured),
      );
      await _idle();
      gateway.heldChecks[0].complete(_networkReport());
      await _idle();
      expect(view(bloc).report!.service, MultiplayerServiceStatus.configured);
      expect(view(bloc).checking, isFalse);

      bloc.add(const MultiplayerNetworkCheckRequested());
      await _idle();
      bloc.add(const MultiplayerNetworkLeft());
      await _idle();
      expect(gateway.checkTokens.last!.isCancelled, isTrue);
      expect(view(bloc).checking, isFalse);
      gateway.heldChecks.last.complete(
        _networkReport(service: MultiplayerServiceStatus.invalid),
      );
      await _idle();
      expect(view(bloc).report!.service, MultiplayerServiceStatus.configured);
    });

    test('port checks and LAN listening wait for the explanation, which is '
        'then kept', () async {
      final gateway = _FakeMultiplayerGateway();
      final bloc = _multiplayerBloc(gateway);
      await _idle();
      gateway.calls.clear();
      bloc
        ..add(const MultiplayerNetworkCheckRequested(checkPorts: true))
        ..add(const MultiplayerLanListeningRequested(true));
      await _idle();
      expect(gateway.calls, isEmpty);
      expect(bloc.state.statusMessage, contains('Read what this check does'));

      bloc.add(const MultiplayerNetworkNoticeRead());
      await _idle();
      expect(gateway.calls, ['change notice']);
      expect(view(bloc).noticeRead, isTrue);
      expect(
        bloc.state.multiplayer.settings.preferences!.needsNetworkNotice,
        isFalse,
      );
      bloc
        ..add(const MultiplayerNetworkCheckRequested(checkPorts: true))
        ..add(const MultiplayerLanListeningRequested(true));
      await _idle();
      expect(gateway.calls, ['change notice', 'check ports', 'lan start']);
      expect(view(bloc).listening, isTrue);
      expect(view(bloc).lan!.status, LanDiscoveryStatus.listening);
      expect(view(bloc).report!.gamePortUdp.result, PortCheckResult.available);
      expect(bloc.state.statusMessage, 'Checked the multiplayer ports.');

      bloc.add(const MultiplayerLanListeningRequested(false));
      await _idle();
      expect(gateway.calls.last, 'lan stop');
      expect(view(bloc).lan!.status, LanDiscoveryStatus.stopped);
    });

    test('read while the preferences cannot be read, the explanation counts '
        'for this session', () async {
      final gateway = _FakeMultiplayerGateway()..unreadable = true;
      final bloc = _multiplayerBloc(gateway);
      await _idle();
      bloc
        ..add(const MultiplayerNetworkNoticeRead())
        ..add(const MultiplayerNetworkCheckRequested(checkPorts: true));
      await _idle();
      expect(gateway.calls, isNot(contains(startsWith('change'))));
      expect(gateway.calls.last, 'check ports');
    });

    test('leaving Diagnostics stops the listening it started', () async {
      final gateway = _FakeMultiplayerGateway(
        preferences: _named('Sam', networkNotice: 1),
      );
      final bloc = _multiplayerBloc(gateway);
      await _idle();
      bloc
        ..add(const LauncherSectionSelected(LauncherSection.diagnostics))
        ..add(const MultiplayerLanListeningRequested(true));
      await _idle();
      expect(gateway.calls.last, 'lan start');
      bloc.add(const LauncherSectionSelected(LauncherSection.home));
      await _idle();
      expect(gateway.calls.last, 'lan stop');
      expect(view(bloc).listening, isFalse);
    });

    test('LAN discovery turned off in Settings shows at once', () async {
      final gateway = _FakeMultiplayerGateway(
        preferences: _named('Sam', networkNotice: 1),
      );
      final bloc = _multiplayerBloc(gateway);
      await _idle();
      bloc.add(const MultiplayerLanListeningRequested(true));
      await _idle();
      bloc.add(
        const MultiplayerPreferencesEdited(
          MultiplayerPreferencesChange(lanDiscovery: false),
        ),
      );
      await _idle();
      expect(view(bloc).lan!.status, LanDiscoveryStatus.disabled);
      expect(bloc.state.statusMessage, contains('LAN discovery is off'));
    });

    test('a check covers the port the player hosts on', () async {
      final gateway = _FakeMultiplayerGateway(
        preferences: MultiplayerPreferences(
          settings: MultiplayerSettings(displayName: 'Sam'),
          hosting: MultiplayerHostingDefaults(name: 'Lab', port: 27300),
          networkNotice: 1,
        ),
      )..hold = true;
      final bloc = _multiplayerBloc(gateway);
      // Before the preferences read, the game's default port.
      bloc.add(const MultiplayerNetworkCheckRequested());
      await _idle();
      expect(gateway.checkedGamePorts, [null]);
      gateway.heldLoads.single.complete(gateway.preferences);
      await _idle();
      bloc.add(const MultiplayerNetworkCheckRequested(checkPorts: true));
      await _idle();
      expect(gateway.checkedGamePorts.last, 27300);
      gateway.heldChecks.last.complete(
        _networkReport(ports: PortCheckResult.available, gamePort: 27300),
      );
      await _idle();
      expect(view(bloc).report!.gamePortTcp.port, 27300);
    });

    test('a check that fails says so in plain words', () async {
      final gateway = _FakeMultiplayerGateway()
        ..failure = StateError('no route to $_plantedAddress');
      final bloc = _multiplayerBloc(gateway, start: false);
      bloc.add(const MultiplayerNetworkCheckRequested());
      await _idle();
      expect(
        view(bloc).problem,
        'The network check could not finish. Try again.',
      );
      expect(view(bloc).checking, isFalse);
      expect(bloc.state.statusMessage, isNot(contains(_plantedAddress)));
    });
  });

  group('Diagnostics → Network, on screen', () {
    testWidgets('reading the state opens nothing, and the first port check '
        'explains itself first', (tester) async {
      final gateway = _FakeMultiplayerGateway();
      await _openNetwork(tester, gateway);
      expect(gateway.calls, contains('check'));
      expect(find.text('Not in this build'), findsOneWidget);
      expect(find.text('Not listening'), findsOneWidget);
      expect(find.text('Not checked'), findsNWidgets(3));

      await tester.tap(find.byKey(const Key('network-check-ports')));
      await tester.pumpAndSettle();
      expect(find.text(NetworkCheckNotice.title), findsOneWidget);
      await tester.tap(find.text('Cancel'));
      await tester.pumpAndSettle();
      expect(gateway.calls, isNot(contains('check ports')));

      await tester.tap(find.byKey(const Key('network-check-ports')));
      await tester.pumpAndSettle();
      await tester.tap(find.text('Continue'));
      await tester.pumpAndSettle();
      expect(
        gateway.calls,
        containsAllInOrder(['change notice', 'check ports']),
      );
      expect(find.text('Available'), findsNWidgets(3));
      expect(
        find.textContaining('not that anyone can reach it'),
        findsOneWidget,
      );

      // Read once, it is not asked again.
      await tester.tap(find.byKey(const Key('network-check-ports')));
      await tester.pumpAndSettle();
      expect(find.text(NetworkCheckNotice.title), findsNothing);
      expect(
        gateway.calls.where((call) => call == 'check ports'),
        hasLength(2),
      );

      // It can be read again at any time.
      await tester.tap(find.byKey(const Key('network-notice-again')));
      await tester.pumpAndSettle();
      expect(find.text(NetworkCheckNotice.title), findsOneWidget);
      await tester.tap(find.text('Close'));
      await tester.pumpAndSettle();
    });

    testWidgets('the explanation and the ports name the port the player '
        'hosts on', (tester) async {
      final gateway = _FakeMultiplayerGateway(
        preferences: MultiplayerPreferences(
          settings: MultiplayerSettings(displayName: 'Sam'),
          hosting: MultiplayerHostingDefaults(name: 'Lab', port: 0),
        ),
      );
      await _openNetwork(tester, gateway);
      expect(find.text('UDP any port'), findsOneWidget);
      expect(find.text('TCP any port'), findsOneWidget);
      await tester.tap(find.byKey(const Key('network-check-ports')));
      await tester.pumpAndSettle();
      expect(
        find.text(
          'A port check briefly opens UDP 27251 on this computer, then '
          'closes it.',
        ),
        findsOneWidget,
      );
      await tester.tap(find.text('Continue'));
      await tester.pumpAndSettle();
      expect(gateway.checkedGamePorts.last, 0);
      // Any free port cannot be checked; LAN discovery's port can.
      expect(find.text('Not checked'), findsNWidgets(2));
      expect(find.text('Available'), findsOneWidget);
    });

    testWidgets('listening shows LAN discovery and its counters, and leaving '
        'stops it', (tester) async {
      final gateway = _FakeMultiplayerGateway(
        preferences: _named('Sam', networkNotice: 1),
      );
      await _openNetwork(tester, gateway);
      await tester.tap(find.byKey(const Key('network-listen')));
      await tester.pumpAndSettle();
      expect(gateway.calls, contains('lan start'));
      expect(find.text('Listening'), findsOneWidget);
      gateway.emitLan(
        LanBrowseSnapshot(
          status: LanDiscoveryStatus.listening,
          pending: 2,
          beyondLocalNetwork: 1,
          counters: const LanDiscoveryCounters(
            datagramsHeard: 41,
            queriesSent: 7,
            repliesRefused: 3,
          ),
        ),
      );
      await tester.pumpAndSettle();
      for (final value in ['41', '7', '3', '2', '1']) {
        expect(find.text(value), findsWidgets);
      }
      expect(find.text('Stop listening'), findsOneWidget);

      await tester.tap(find.text('Home').first);
      await tester.pumpAndSettle();
      expect(gateway.calls.last, 'lan stop');
    });

    testWidgets('a failed check and an unavailable LAN say so plainly', (
      tester,
    ) async {
      final gateway = _FakeMultiplayerGateway()
        ..lan = LanBrowseSnapshot(
          status: LanDiscoveryStatus.unavailable,
          problem: LanDiscoveryProblem.portInUse,
        );
      await _openNetwork(tester, gateway);
      expect(find.text('Unavailable'), findsOneWidget);
      expect(find.textContaining('Another program is using'), findsOneWidget);
      gateway.failure = StateError('reset by $_plantedEndpoint');
      await tester.tap(find.byKey(const Key('network-refresh')));
      await tester.pumpAndSettle();
      expect(find.byKey(const Key('network-problem')), findsOneWidget);
      expect(find.textContaining(_plantedAddress), findsNothing);
      expect(find.textContaining('2001:db8'), findsNothing);
    });
  });
}
