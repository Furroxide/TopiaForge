part of 'widget_test.dart';

/// Opens Settings → Multiplayer in the launcher over [gateway].
Future<LauncherBloc> _openMultiplayerSettings(
  WidgetTester tester,
  _FakeMultiplayerGateway gateway, {
  LauncherSnapshot? snapshot,
  SessionLinkHandlingGateway? links,
  Size size = const Size(1280, 1600),
  bool settle = true,
}) async {
  tester.view.physicalSize = size;
  tester.view.devicePixelRatio = 1;
  addTearDown(tester.view.reset);
  await tester.pumpWidget(
    TopiaForgeLauncherApp(
      repository: _FakeLauncherRepository(
        snapshot: snapshot ?? _readySnapshot(),
      ),
      multiplayer: gateway,
      sessionLinkHandling: links,
    ),
  );
  await (settle ? tester.pumpAndSettle() : tester.pump());
  final bloc = BlocProvider.of<LauncherBloc>(
    tester.element(find.byType(LauncherShell)),
  );
  bloc.add(const LauncherSectionSelected(LauncherSection.settings));
  await (settle ? tester.pumpAndSettle() : tester.pump());
  await tester.tap(find.widgetWithText(Tab, 'Multiplayer'));
  if (settle) {
    await tester.pumpAndSettle();
  } else {
    // Frames enough for the tab to change, while held answers stay held.
    for (var frame = 0; frame < 10; frame++) {
      await tester.pump(const Duration(milliseconds: 100));
    }
  }
  return bloc;
}

Finder _field(String key) =>
    find.descendant(of: find.byKey(Key(key)), matching: find.byType(TextField));

Finder _saveOf(String key) => find.descendant(
  of: find.byKey(Key(key)),
  matching: find.widgetWithText(FilledButton, 'Save'),
);

bool _enabled(WidgetTester tester, Finder button) =>
    tester.widget<ButtonStyleButton>(button).onPressed != null;

void registerMultiplayerSettingsWidgetTests() {
  group('Settings → Multiplayer, on screen', () {
    testWidgets('shows loading until the preferences read', (tester) async {
      final gateway = _FakeMultiplayerGateway()..hold = true;
      await _openMultiplayerSettings(tester, gateway, settle: false);
      expect(find.byKey(const Key('multiplayer-loading')), findsOneWidget);
      gateway.heldLoads.single.complete(_named('Sam'));
      await tester.pump();
      await tester.pump();
      expect(find.byKey(const Key('multiplayer-loading')), findsNothing);
      expect(find.text('Sam'), findsOneWidget);
    });

    testWidgets('shows identity, privacy, hosting, voice and links', (
      tester,
    ) async {
      await _openMultiplayerSettings(tester, _FakeMultiplayerGateway());
      for (final title in [
        'Identity',
        'Privacy',
        'Hosting defaults',
        'Voice chat',
        'Links',
      ]) {
        expect(find.text(title), findsWidgets, reason: title);
      }
      expect(find.byKey(const Key('hide-my-ip')), findsOneWidget);
      expect(
        find.text(MultiplayerPrivacyText.disclosureHidden),
        findsOneWidget,
      );
      expect(
        find.text(MultiplayerPrivacyText.disclosureChoice),
        findsOneWidget,
      );
      expect(find.textContaining('not read the address disclosure'), findsOne);
      expect(find.text('27250'), findsOneWidget);
      expect(find.byKey(const Key('multiplayer-lock')), findsNothing);
      // Direct play is never a setting: nothing here can turn it on.
      expect(find.textContaining('Allow for this session'), findsNothing);
      expect(
        find.descendant(
          of: find.byKey(const Key('hide-my-ip')),
          matching: find.byType(Switch),
        ),
        findsNothing,
      );
    });

    testWidgets('a display name is 2 to 24 characters, with the reason shown '
        'as it is typed', (tester) async {
      final gateway = _FakeMultiplayerGateway();
      await _openMultiplayerSettings(tester, gateway);
      final field = _field('multiplayer-display-name');
      final save = _saveOf('multiplayer-display-name');
      for (final (typed, reason) in [
        ('S', 'Use at least 2 characters.'),
        ('S' * 25, 'Use at most 24 characters.'),
        (' Sam', 'Take out the space at the start or the end.'),
        ('Sa\u0007m', 'Use letters, numbers, spaces and symbols only.'),
        ('Sam￿', 'Use letters, numbers, spaces and symbols only.'),
      ]) {
        await tester.enterText(field, typed);
        await tester.pump();
        expect(find.text(reason), findsOneWidget, reason: typed);
        expect(_enabled(tester, save), isFalse, reason: typed);
      }
      await tester.enterText(field, 'Al');
      await tester.pump();
      expect(_enabled(tester, save), isTrue);
      await tester.enterText(field, 'R' * 24);
      await tester.pump();
      expect(_enabled(tester, save), isTrue);
      await tester.tap(save);
      await tester.pumpAndSettle();
      expect(gateway.calls, contains('change name ${'R' * 24}'));

      // Enter saves too.
      await tester.enterText(field, 'Robin');
      await tester.testTextInput.receiveAction(TextInputAction.done);
      await tester.pumpAndSettle();
      expect(gateway.calls, contains('change name Robin'));
      expect(find.text('Multiplayer settings saved.'), findsOneWidget);
    });

    testWidgets('a hosting port is 0 to 65535, but not the LAN port', (
      tester,
    ) async {
      final gateway = _FakeMultiplayerGateway();
      await _openMultiplayerSettings(tester, gateway);
      final field = _field('multiplayer-hosting-port');
      final save = _saveOf('multiplayer-hosting-port');
      await tester.enterText(field, '27251');
      await tester.pump();
      expect(find.textContaining('is the LAN discovery port'), findsOneWidget);
      expect(_enabled(tester, save), isFalse);
      await tester.enterText(field, '70000');
      await tester.pump();
      expect(find.text('Enter a number from 0 to 65535.'), findsOneWidget);
      await tester.enterText(field, '0');
      await tester.pump();
      await tester.tap(save);
      await tester.pumpAndSettle();
      expect(gateway.calls, contains('change port 0'));

      await tester.tap(find.byKey(const Key('multiplayer-upnp')));
      await tester.pumpAndSettle();
      await tester.tap(find.byKey(const Key('multiplayer-lan-discovery')));
      await tester.pumpAndSettle();
      expect(
        gateway.calls,
        containsAllInOrder(['change upnp true', 'change lan false']),
      );
    });

    testWidgets('port-only edits wait until the game defaults have been read', (
      tester,
    ) async {
      final gateway = _FakeMultiplayerGateway(
        preferences: MultiplayerPreferences(
          settings: MultiplayerSettings(displayName: 'Sam'),
        ),
      );
      await _openMultiplayerSettings(tester, gateway);
      expect(
        tester.widget<TextField>(_field('multiplayer-hosting-port')).enabled,
        isFalse,
      );
      expect(
        find.textContaining('Read the game hosting defaults'),
        findsOneWidget,
      );
      expect(
        gateway.calls.where((call) => call.startsWith('change port')),
        isEmpty,
      );
    });

    testWidgets('the disclosure reads again, and recording it needs its '
        'button', (tester) async {
      final gateway = _FakeMultiplayerGateway();
      await _openMultiplayerSettings(tester, gateway);
      await tester.tap(find.byKey(const Key('read-disclosure')));
      await tester.pumpAndSettle();
      expect(
        find.text(MultiplayerPrivacyText.disclosureExposure),
        findsOneWidget,
      );
      await tester.sendKeyEvent(LogicalKeyboardKey.escape);
      await tester.pumpAndSettle();
      expect(gateway.calls, isNot(contains('change disclosure')));

      await tester.tap(find.byKey(const Key('read-disclosure')));
      await tester.pumpAndSettle();
      await tester.tap(find.text(MultiplayerPrivacyText.disclosureButton));
      await tester.pumpAndSettle();
      expect(gateway.calls, contains('change disclosure'));
      expect(
        find.text('You read the address disclosure (version 1).'),
        findsOne,
      );
      expect(find.text('Read it again'), findsOneWidget);

      // Reading it again records nothing more.
      final changes = gateway.calls.length;
      await tester.tap(find.byKey(const Key('read-disclosure')));
      await tester.pumpAndSettle();
      await tester.tap(find.text(MultiplayerPrivacyText.disclosureButton));
      await tester.pumpAndSettle();
      expect(gateway.calls, hasLength(changes));
    });

    testWidgets('voice stays off until consent, which can be withdrawn', (
      tester,
    ) async {
      final gateway = _FakeMultiplayerGateway();
      await _openMultiplayerSettings(tester, gateway);
      expect(find.byKey(const Key('voice-state')), findsOneWidget);
      await tester.tap(find.byKey(const Key('voice-consent')));
      await tester.pumpAndSettle();
      expect(find.text(MultiplayerPrivacyText.voiceBody), findsOneWidget);
      await tester.tap(find.text(MultiplayerPrivacyText.voiceNotNowButton));
      await tester.pumpAndSettle();
      expect(gateway.calls, isNot(contains(startsWith('change consent'))));

      await tester.tap(find.byKey(const Key('voice-consent')));
      await tester.pumpAndSettle();
      await tester.tap(find.text(MultiplayerPrivacyText.voiceOnButton));
      await tester.pumpAndSettle();
      expect(gateway.calls, contains('change consent true'));
      expect(
        tester
            .widget<SwitchListTile>(find.byKey(const Key('voice-switch')))
            .value,
        isTrue,
      );
      await tester.tap(find.byKey(const Key('voice-switch')));
      await tester.pumpAndSettle();
      expect(gateway.calls, contains('change voice false'));
      await tester.tap(find.byKey(const Key('voice-withdraw')));
      await tester.pumpAndSettle();
      expect(gateway.calls, contains('change consent false'));
      expect(find.byKey(const Key('voice-consent')), findsOneWidget);
    });

    testWidgets('while the game runs, one line says why the rest is '
        'read-only, and links still change', (tester) async {
      final gateway = _FakeMultiplayerGateway()..running = GameRunState.running;
      final links = _FakeSessionLinkHandling();
      await _openMultiplayerSettings(tester, gateway, links: links);
      expect(find.byKey(const Key('multiplayer-lock')), findsOneWidget);
      expect(
        find.textContaining('You can change them once the game is closed.'),
        findsOneWidget,
      );
      expect(
        tester.widget<TextField>(_field('multiplayer-display-name')).enabled,
        isFalse,
      );
      expect(
        tester.widget<TextField>(_field('multiplayer-hosting-port')).enabled,
        isFalse,
      );
      for (final key in ['multiplayer-upnp', 'multiplayer-lan-discovery']) {
        expect(
          tester.widget<SwitchListTile>(find.byKey(Key(key))).onChanged,
          isNull,
          reason: key,
        );
      }
      expect(_enabled(tester, find.byKey(const Key('voice-consent'))), isFalse);
      // Reading the disclosure is never locked.
      expect(
        _enabled(tester, find.byKey(const Key('read-disclosure'))),
        isTrue,
      );
      await tester.tap(find.byKey(const Key('multiplayer-links')));
      await tester.pumpAndSettle();
      expect(links.calls, contains('set false'));

      // Closed, Check again lifts the lock.
      gateway.running = GameRunState.notRunning;
      await tester.tap(find.text('Check again'));
      await tester.pumpAndSettle();
      expect(find.byKey(const Key('multiplayer-lock')), findsNothing);
      expect(
        tester.widget<TextField>(_field('multiplayer-display-name')).enabled,
        isTrue,
      );
    });

    testWidgets('settings that cannot be read show only Try again', (
      tester,
    ) async {
      final gateway = _FakeMultiplayerGateway()..unreadable = true;
      await _openMultiplayerSettings(tester, gateway);
      expect(find.byKey(const Key('multiplayer-unreadable')), findsOneWidget);
      expect(find.text('Identity'), findsNothing);
      expect(find.text('Player'), findsNothing);
      gateway.unreadable = false;
      await tester.tap(find.text('Try again'));
      await tester.pumpAndSettle();
      expect(find.byKey(const Key('multiplayer-unreadable')), findsNothing);
      expect(find.text('Identity'), findsOneWidget);
    });

    testWidgets('recovered settings and a failed save are warned about', (
      tester,
    ) async {
      final gateway = _FakeMultiplayerGateway(
        preferences: MultiplayerPreferences(
          settings: MultiplayerSettings(displayName: 'Sam'),
          health: MultiplayerPreferencesHealth.salvaged,
        ),
      );
      await _openMultiplayerSettings(tester, gateway);
      expect(find.byKey(const Key('multiplayer-recovered')), findsOneWidget);
      gateway.failSaves = true;
      await tester.tap(find.byKey(const Key('multiplayer-upnp')));
      await tester.pumpAndSettle();
      expect(find.byKey(const Key('multiplayer-problem')), findsOneWidget);
      expect(find.textContaining('could not be saved'), findsWidgets);
      expect(find.textContaining(_plantedAddress), findsNothing);
    });

    testWidgets('without an install, the settings say where they will go', (
      tester,
    ) async {
      await _openMultiplayerSettings(
        tester,
        _FakeMultiplayerGateway(),
        snapshot: _replaceGameInstall(_readySnapshot(), null),
      );
      expect(find.byKey(const Key('multiplayer-no-install')), findsOneWidget);
      expect(find.byKey(const Key('multiplayer-lock')), findsNothing);
    });
  });
}
