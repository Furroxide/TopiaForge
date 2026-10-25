part of 'join_flow_test.dart';

/// The launcher on screen with [gateway] and [repository].
Future<(_JoinGateway, _JoinRepository)> _openApp(
  WidgetTester tester, {
  _JoinGateway? gateway,
  _JoinRepository? repository,
  Size size = const Size(1280, 900),
  SessionLinkInbox? inbox,
}) async {
  tester.view.physicalSize = size;
  tester.view.devicePixelRatio = 1;
  addTearDown(tester.view.reset);
  final multiplayer = gateway ?? _JoinGateway();
  final launcher = repository ?? _JoinRepository(install: _install);
  await tester.pumpWidget(
    TopiaForgeLauncherApp(
      repository: launcher,
      multiplayer: multiplayer,
      sessionLinks: inbox?.arrivals,
    ),
  );
  await tester.pumpAndSettle();
  return (multiplayer, launcher);
}

LauncherBloc _blocIn(WidgetTester tester) =>
    BlocProvider.of<LauncherBloc>(tester.element(find.byType(LauncherShell)));

/// Whether the widget with [key], or one inside it, holds the focus.
bool _focused(Key key) {
  final focused = FocusManager.instance.primaryFocus?.context;
  if (focused is! Element) return false;
  var found = focused.widget.key == key;
  focused.visitAncestorElements((ancestor) {
    if (ancestor.widget.key == key) found = true;
    return !found;
  });
  return found;
}

/// Opens the server link's join and waits at its consent sheet.
Future<void> _toSheet(WidgetTester tester) async {
  _blocIn(tester).add(MultiplayerJoinRequested(JoinLinkTarget(_serverLink)));
  await tester.pumpAndSettle();
  expect(find.byKey(const Key('join-consent-sheet')), findsOneWidget);
}

/// What the app last put on the clipboard.
String? _copied;

void _captureClipboard(WidgetTester tester) {
  _copied = null;
  tester.binding.defaultBinaryMessenger.setMockMethodCallHandler(
    SystemChannels.platform,
    (call) async {
      if (call.method == 'Clipboard.setData') {
        _copied = (call.arguments as Map)['text'] as String?;
      }
      return null;
    },
  );
  addTearDown(
    () => tester.binding.defaultBinaryMessenger.setMockMethodCallHandler(
      SystemChannels.platform,
      null,
    ),
  );
}

void registerJoinWidgetTests() {
  group('The join dialog', () {
    testWidgets('a link opens it at the disclosure, whose one button has the '
        'focus', (tester) async {
      final inbox = SessionLinkInbox();
      final (gateway, _) = await _openApp(
        tester,
        gateway: _JoinGateway(
          preferences: MultiplayerPreferences(
            settings: MultiplayerSettings(displayName: 'Sam'),
          ),
        ),
        inbox: inbox,
      );
      inbox.deliver(
        SessionLinkArrival(
          _serverLink.canonical,
          source: SessionLinkSource.forwarded,
        ),
      );
      await tester.pumpAndSettle();

      expect(find.byKey(const Key('join-question-disclosure')), findsOneWidget);
      for (final text in [
        MultiplayerPrivacyText.disclosureTitle,
        MultiplayerPrivacyText.disclosureExposure,
        MultiplayerPrivacyText.disclosureHidden,
        MultiplayerPrivacyText.disclosureChoice,
      ]) {
        expect(find.text(text), findsOneWidget);
      }
      expect(_focused(const Key('join-understand')), isTrue);
      await tester.sendKeyEvent(LogicalKeyboardKey.enter);
      await tester.pumpAndSettle();

      expect(gateway.calls, contains('acknowledge'));
      expect(find.byKey(const Key('join-consent-sheet')), findsOneWidget);
    });

    testWidgets('Escape on the disclosure closes it and joins nothing', (
      tester,
    ) async {
      final (gateway, _) = await _openApp(
        tester,
        gateway: _JoinGateway(
          preferences: MultiplayerPreferences(settings: MultiplayerSettings()),
        ),
      );
      _blocIn(
        tester,
      ).add(MultiplayerJoinRequested(JoinLinkTarget(_serverLink)));
      await tester.pumpAndSettle();
      await tester.sendKeyEvent(LogicalKeyboardKey.escape);
      await tester.pumpAndSettle();

      expect(find.byKey(const Key('join-dialog')), findsNothing);
      expect(find.text('Nothing was joined.'), findsOneWidget);
      expect(gateway.calls.where((call) => call.startsWith('answer')), isEmpty);
    });

    testWidgets('the direct prompt puts Cancel first; Enter keeps Hide my IP '
        'on', (tester) async {
      final (gateway, _) = await _openApp(tester);
      _blocIn(tester).add(
        MultiplayerJoinRequested(
          _publicAddress,
          origin: JoinFlowOrigin.address,
        ),
      );
      await tester.pumpAndSettle();

      expect(find.text(MultiplayerPrivacyText.directTitle), findsOneWidget);
      expect(find.text(MultiplayerPrivacyText.directBody), findsOneWidget);
      expect(
        find.widgetWithText(
          FilledButton,
          MultiplayerPrivacyText.directCancelButton,
        ),
        findsOneWidget,
      );
      expect(_focused(const Key('direct-connection-cancel')), isTrue);
      await tester.sendKeyEvent(LogicalKeyboardKey.enter);
      await tester.pumpAndSettle();

      expect(find.byKey(const Key('join-dialog')), findsNothing);
      expect(gateway.calls.where((call) => call.startsWith('answer')), isEmpty);
    });

    testWidgets('the consent sheet shows the plan, what its mods may do, the '
        'approved lines and the profile', (tester) async {
      await _openApp(tester);
      await _toSheet(tester);

      expect(find.text("Join Sam's Sandbox?"), findsOneWidget);
      expect(find.text('Mods (2)'), findsOneWidget);
      expect(find.text('Zombies'), findsOneWidget);
      expect(find.text('tf.zombies@2.1.0'), findsOneWidget);
      expect(find.text('Download from Community Mods'), findsOneWidget);
      expect(find.text('tf.sandbox@1.4.0'), findsOneWidget);
      expect(find.text('Installed'), findsNWidgets(2));
      expect(find.text('${_sha('zombies').substring(0, 12)}…'), findsOneWidget);
      expect(find.byTooltip(_sha('zombies')), findsOneWidget);
      expect(find.text('3.0 MB'), findsOneWidget);
      expect(find.textContaining('Downloads 1 mod, 3.0 MB'), findsOneWidget);
      expect(find.text('hud'), findsOneWidget);
      expect(find.text('network'), findsOneWidget);
      expect(find.byKey(const Key('join-code-warning')), findsOneWidget);
      expect(
        find.text(MultiplayerPrivacyText.sheetPrivacyHidden),
        findsOneWidget,
      );
      expect(find.text(MultiplayerPrivacyText.sheetVoiceOff), findsOneWidget);
      expect(
        find.text("Creates profile 'Server · Sam's Sandbox'"),
        findsOneWidget,
      );
      expect(find.byKey(const Key('join-closes-game')), findsNothing);
      // Enter never joins: the focus starts on Cancel.
      expect(_focused(const Key('join-consent-cancel')), isTrue);
      await tester.sendKeyEvent(LogicalKeyboardKey.enter);
      await tester.pumpAndSettle();
      expect(find.text('Join cancelled'), findsOneWidget);
    });

    testWidgets('Join consents, and the progress rail takes over', (
      tester,
    ) async {
      final gateway = _JoinGateway();
      gateway.hold(JoinStage.downloading);
      await _openApp(tester, gateway: gateway);
      await _toSheet(tester);
      await tester.tap(find.byKey(const Key('join-consent-join')));
      await tester.pumpAndSettle();

      expect(find.byKey(const Key('join-progress')), findsOneWidget);
      expect(find.text("Joining Sam's Sandbox"), findsOneWidget);
      for (final (_, label) in joinSteps) {
        expect(find.text(label), findsOneWidget, reason: label);
      }
      expect(find.text('Starting the downloads.'), findsOneWidget);
      expect(_focused(const Key('join-cancel')), isTrue);
      await tester.tap(find.byKey(const Key('join-cancel')));
      await tester.pumpAndSettle();
      expect(find.text('Join cancelled'), findsOneWidget);
    });

    testWidgets('the sheet says when joining closes the game this launcher '
        'started, and the consent covers it', (tester) async {
      await _openApp(
        tester,
        gateway: _JoinGateway()..running = JoinGameRunning.owned,
      );
      await _toSheet(tester);
      expect(find.byKey(const Key('join-closes-game')), findsOneWidget);

      await tester.tap(find.byKey(const Key('join-consent-join')));
      await tester.pumpAndSettle();
      expect(
        _flowOf(_blocIn(tester))!.join!.closeOwnedGameAcknowledged,
        isTrue,
      );
    });

    testWidgets('a game the launcher did not start is named on the sheet', (
      tester,
    ) async {
      await _openApp(
        tester,
        gateway: _JoinGateway()..running = JoinGameRunning.foreign,
      );
      await _toSheet(tester);
      expect(find.byKey(const Key('join-foreign-game')), findsOneWidget);
      await tester.tap(find.byKey(const Key('join-consent-join')));
      await tester.pumpAndSettle();
      expect(
        _flowOf(_blocIn(tester))!.join!.closeOwnedGameAcknowledged,
        isFalse,
      );
    });

    testWidgets('the voice line is on only with the player\'s consent and the '
        'server\'s voice, and a rejoin updates its profile', (tester) async {
      await _openApp(
        tester,
        gateway: _JoinGateway(
          preferences: MultiplayerPreferences(
            settings: MultiplayerSettings(
              displayName: 'Sam',
            ).acknowledgePrivacyDisclosure().giveVoiceConsent().withVoice(true),
          ),
        ),
        repository: _JoinRepository(
          install: _install,
          profiles: [
            LauncherProfile.defaultProfile(),
            LauncherProfile(
              id: JoinPlanner.derivedProfileId(_serverId),
              name: "Server · Sam's Sandbox",
              inheritManagerModState: false,
            ),
          ],
        ),
      );
      await _toSheet(tester);
      expect(find.text(MultiplayerPrivacyText.sheetVoiceOn), findsOneWidget);
      expect(
        find.text("Updates profile 'Server · Sam's Sandbox'"),
        findsOneWidget,
      );
    });

    testWidgets('Join by address takes a host and port, says how passwords '
        'work, and remembers what was typed', (tester) async {
      await _openApp(tester);
      await tester.tap(find.byKey(const Key('join-by-address')));
      await tester.pumpAndSettle();
      expect(find.byKey(const Key('direct-connect-dialog')), findsOneWidget);
      expect(
        find.textContaining('Robotopia asks for it when you join'),
        findsOneWidget,
      );
      expect(_focused(const Key('direct-connect-host')), isTrue);

      await tester.enterText(
        find.byKey(const Key('direct-connect-host')),
        'not a host!',
      );
      await tester.tap(find.byKey(const Key('direct-connect-join')));
      await tester.pump();
      expect(find.byKey(const Key('direct-connect-problem')), findsOneWidget);

      await tester.enterText(
        find.byKey(const Key('direct-connect-host')),
        '192.168.1.20',
      );
      await tester.enterText(
        find.byKey(const Key('direct-connect-port')),
        '27260',
      );
      await tester.tap(find.byKey(const Key('direct-connect-join')));
      await tester.pumpAndSettle();

      final flow = _flowOf(_blocIn(tester))!;
      expect(flow.origin, JoinFlowOrigin.address);
      expect((flow.target as JoinAddressTarget).port, 27260);
      expect(find.byKey(const Key('join-consent-sheet')), findsOneWidget);
      // The address shows in the dialog it was typed in, nowhere else.
      expect(find.textContaining('192.168.1.20'), findsNothing);
      await tester.tap(find.byKey(const Key('join-consent-cancel')));
      await tester.pumpAndSettle();
      await tester.tap(find.byKey(const Key('join-close')));
      await tester.pumpAndSettle();

      await tester.tap(find.byKey(const Key('join-by-address')));
      await tester.pumpAndSettle();
      expect(find.text('192.168.1.20:27260'), findsOneWidget);
      await tester.tap(find.byKey(const Key('direct-connect-history-0')));
      await tester.pump();
      expect(
        tester
            .widget<TextField>(find.byKey(const Key('direct-connect-port')))
            .controller!
            .text,
        '27260',
      );
    });
  });

  group('Every failure card', () {
    for (final code in JoinFailureCode.values) {
      final stage = JoinSession.failuresByStage.entries
          .where((entry) => entry.value.contains(code))
          .map((entry) => entry.key)
          .firstOrNull;
      if (stage == null) continue;
      testWidgets('${code.name}: its sentence, what to do, Retry only where '
          'it helps, and Copy details', (tester) async {
        _captureClipboard(tester);
        final gateway = _JoinGateway();
        final repository = _JoinRepository(install: _install);
        await _openApp(tester, gateway: gateway, repository: repository);
        await _failWidget(tester, gateway, repository, stage, code);

        final failure = _flowOf(_blocIn(tester))!.join!.outcome!.failure!;
        final text = JoinFailureText.of(failure);
        expect(find.byKey(const Key('join-failure-card')), findsOneWidget);
        expect(find.text(text.title), findsOneWidget);
        expect(find.text(text.bodyFor(failure)), findsOneWidget);
        if (text.nextStep != null) {
          expect(find.text('What to do: ${text.nextStep}'), findsOneWidget);
        }
        expect(
          find.byKey(const Key('join-retry')),
          text.retry ? findsOneWidget : findsNothing,
        );
        final expectedFocus = text.retry
            ? 'join-retry'
            : switch (text.action) {
                JoinFailureAction.allowDirect => 'join-allow-direct',
                JoinFailureAction.trust => 'join-trust',
                _ => 'join-close',
              };
        expect(_focused(Key(expectedFocus)), isTrue, reason: expectedFocus);
        await tester.tap(find.byKey(const Key('join-copy-details')));
        await tester.pumpAndSettle();
        expect(_copied, contains('Reason: ${code.name}'));
        expect(_copied, isNot(contains(_planted)));
        expect(find.text('Copied'), findsOneWidget);

        // The card fits the smallest window with text at twice its size.
        tester.view.physicalSize = const Size(800, 600);
        tester.platformDispatcher.textScaleFactorTestValue = 2;
        addTearDown(tester.platformDispatcher.clearTextScaleFactorTestValue);
        await tester.pumpAndSettle();
        await tester.drag(
          find.byKey(const Key('join-scroll')),
          const Offset(0, -6000),
        );
        await tester.pumpAndSettle();
        expect(tester.takeException(), isNull);
      });
    }
  });
}

/// Drives the app's join to fail as [code] at [stage].
Future<void> _failWidget(
  WidgetTester tester,
  _JoinGateway gateway,
  _JoinRepository repository,
  JoinStage stage,
  JoinFailureCode code,
) async {
  switch (stage) {
    case JoinStage.launching:
      gateway.launch = (_) => JoinLaunchRefused(_failure(code) as JoinFailed);
    case JoinStage.connecting || JoinStage.synchronizing:
      break;
    default:
      gateway.answers[stage] = (_) => _failure(code);
  }
  _blocIn(tester).add(
    MultiplayerJoinRequested(
      code == JoinFailureCode.identityChanged
          ? JoinAddressTarget(host: '192.168.1.20', port: 27250)
          : JoinLinkTarget(_serverLink),
    ),
  );
  await tester.pumpAndSettle();
  if (stage.index > JoinStage.awaitingConsent.index) {
    await tester.tap(find.byKey(const Key('join-consent-join')));
    await tester.pumpAndSettle();
  }
  if (stage == JoinStage.connecting || stage == JoinStage.synchronizing) {
    final failure = LaunchMultiplayerFailure.values.firstWhere(
      (item) => item.joinFailureCode == code,
    );
    final reached = stage == JoinStage.synchronizing
        ? LaunchSessionState.synchronizing
        : LaunchSessionState.connecting;
    for (var round = 0; round < 2; round++) {
      repository.report(_activity(state: reached));
      await tester.pumpAndSettle();
      repository.report(
        _activity(failure: failure, detail: 'The game said $_planted.'),
      );
      await tester.pumpAndSettle();
      // Only a second change of admission ends the join.
      if (code != JoinFailureCode.admissionChanged) break;
    }
  }
}
