part of 'widget_test.dart';

/// Each state Settings → Multiplayer shows, on a gateway set up for it.
Map<String, _FakeMultiplayerGateway Function()> _settingsStates() => {
  'loading': () => _FakeMultiplayerGateway()..hold = true,
  'editable': _FakeMultiplayerGateway.new,
  'voice on': () => _FakeMultiplayerGateway(
    preferences: MultiplayerPreferences(
      settings: MultiplayerSettings(
        displayName: 'W' * 24,
      ).acknowledgePrivacyDisclosure().giveVoiceConsent().withVoice(true),
    ),
  ),
  'read-only': () => _FakeMultiplayerGateway()..running = GameRunState.running,
  'cannot tell': () =>
      _FakeMultiplayerGateway()..running = GameRunState.unknown,
  'unreadable': () => _FakeMultiplayerGateway()..unreadable = true,
  'reset': () => _FakeMultiplayerGateway(
    preferences: MultiplayerPreferences(
      settings: MultiplayerSettings(),
      health: MultiplayerPreferencesHealth.reset,
    ),
  ),
};

/// Each state Diagnostics → Network shows, on a gateway set up for it.
Map<String, _FakeMultiplayerGateway Function()> _networkStates() => {
  'reading': () => _FakeMultiplayerGateway()..hold = true,
  'ports checked': () =>
      _FakeMultiplayerGateway(preferences: _named('Sam', networkNotice: 1))
        ..report = _networkReport(ports: PortCheckResult.inUse),
  'listening': () => _FakeMultiplayerGateway()
    ..lan = LanBrowseSnapshot(
      status: LanDiscoveryStatus.listening,
      pending: 3,
      counters: const LanDiscoveryCounters(
        datagramsHeard: 123456,
        queriesSent: 98765,
        repliesRefused: 4321,
      ),
    ),
  'unavailable': () => _FakeMultiplayerGateway()
    ..lan = LanBrowseSnapshot(
      status: LanDiscoveryStatus.unavailable,
      problem: LanDiscoveryProblem.permissionDenied,
    ),
  'failed': () => _FakeMultiplayerGateway()..failure = StateError('reset'),
};

/// The key of [keys] whose widget holds the keyboard focus, or null.
Key? _focusedAmong(List<Key> keys) {
  final focused = FocusManager.instance.primaryFocus?.context;
  if (focused is! Element) return null;
  Key? found = keys.contains(focused.widget.key) ? focused.widget.key : null;
  focused.visitAncestorElements((ancestor) {
    if (found != null) return false;
    if (keys.contains(ancestor.widget.key)) found = ancestor.widget.key;
    return found == null;
  });
  return found;
}

/// WCAG contrast between two opaque colours.
double _contrast(Color a, Color b) {
  double luminance(Color color) {
    double channel(double value) => value <= 0.03928
        ? value / 12.92
        : math.pow((value + 0.055) / 1.055, 2.4).toDouble();
    return 0.2126 * channel(color.r) +
        0.7152 * channel(color.g) +
        0.0722 * channel(color.b);
  }

  final (light, dark) = luminance(a) > luminance(b)
      ? (luminance(a), luminance(b))
      : (luminance(b), luminance(a));
  return (light + 0.05) / (dark + 0.05);
}

void registerMultiplayerLayoutTests() {
  group('Multiplayer screens keep their layout', () {
    for (final (size, scale) in [
      (const Size(800, 600), 1.0),
      (const Size(800, 600), 2.0),
    ]) {
      for (final MapEntry(key: name, value: make)
          in _settingsStates().entries) {
        testWidgets(
          'Settings → Multiplayer, $name, at $size and text x$scale',
          (tester) async {
            tester.platformDispatcher.textScaleFactorTestValue = scale;
            addTearDown(
              tester.platformDispatcher.clearTextScaleFactorTestValue,
            );
            await _openMultiplayerSettings(
              tester,
              make(),
              size: size,
              settle: name != 'loading',
            );
            expect(tester.takeException(), isNull);
            await tester.drag(
              find.byKey(const Key('multiplayer-settings-scroll')),
              const Offset(0, -4000),
            );
            await tester.pump();
            expect(tester.takeException(), isNull);
          },
        );
      }
      for (final MapEntry(key: name, value: make) in _networkStates().entries) {
        testWidgets('Diagnostics → Network, $name, at $size and text x$scale', (
          tester,
        ) async {
          tester.platformDispatcher.textScaleFactorTestValue = scale;
          addTearDown(tester.platformDispatcher.clearTextScaleFactorTestValue);
          final gateway = make();
          await _openNetwork(
            tester,
            gateway,
            size: size,
            settle: name != 'reading',
          );
          if (name == 'ports checked') {
            await tester.tap(find.byKey(const Key('network-check-ports')));
            await tester.pumpAndSettle();
          }
          expect(tester.takeException(), isNull);
          await tester.drag(
            find.byKey(const Key('network-diagnostics-scroll')),
            const Offset(0, -4000),
          );
          await tester.pump();
          expect(tester.takeException(), isNull);
        });
      }
    }

    for (final (theme, features, brightness) in [
      (
        'light',
        const FakeAccessibilityFeatures(disableAnimations: true),
        Brightness.light,
      ),
      (
        'high contrast',
        const FakeAccessibilityFeatures(
          disableAnimations: true,
          highContrast: true,
        ),
        Brightness.light,
      ),
      (
        'a dark system',
        const FakeAccessibilityFeatures(disableAnimations: true),
        Brightness.dark,
      ),
    ]) {
      testWidgets('render in the $theme theme, readable', (tester) async {
        tester.platformDispatcher
          ..accessibilityFeaturesTestValue = features
          ..platformBrightnessTestValue = brightness;
        addTearDown(tester.platformDispatcher.clearPlatformBrightnessTestValue);
        await _openMultiplayerSettings(
          tester,
          _FakeMultiplayerGateway()..running = GameRunState.running,
        );
        final pane = tester.element(find.byKey(const Key('multiplayer-lock')));
        final colors = Theme.of(pane);
        // The launcher has a light theme and a high-contrast one; with the
        // system dark it keeps the light one, so nothing here turns dark.
        expect(colors.brightness, Brightness.light);
        expect(
          colors.colorScheme.outline,
          theme == 'high contrast'
              ? TopiaForgePalette.darkPanel
              : TopiaForgePalette.border,
        );
        expect(tester.takeException(), isNull);
        // Notice text stays readable on its tinted background, and its icon
        // stays visible, in every tone.
        for (final tone in StatusTone.values) {
          final tint = switch (tone) {
            StatusTone.good => TopiaForgePalette.good,
            StatusTone.info => TopiaForgePalette.accentDark,
            StatusTone.warning => TopiaForgePalette.launchDark,
            StatusTone.danger => TopiaForgePalette.danger,
            StatusTone.neutral => TopiaForgePalette.mutedText,
          };
          final background = Color.alphaBlend(
            tint.withValues(alpha: 0.08),
            TopiaForgePalette.surface,
          );
          expect(
            _contrast(TopiaForgePalette.text, background),
            greaterThanOrEqualTo(4.5),
            reason: '$tone text',
          );
          expect(
            _contrast(tint, background),
            greaterThanOrEqualTo(3),
            reason: '$tone icon',
          );
        }
      });
    }

    testWidgets('the keyboard reaches every control in reading order', (
      tester,
    ) async {
      final gateway = _FakeMultiplayerGateway();
      await _openMultiplayerSettings(
        tester,
        gateway,
        links: _FakeSessionLinkHandling(),
      );
      const order = [
        Key('multiplayer-display-name'),
        Key('read-disclosure'),
        Key('multiplayer-hosting-port'),
        Key('multiplayer-upnp'),
        Key('multiplayer-lan-discovery'),
        Key('voice-consent'),
        Key('multiplayer-links'),
      ];
      await tester.tap(_field('multiplayer-display-name'));
      await tester.pump();
      final seen = <Key>[];
      for (var press = 0; press < 20 && seen.length < order.length; press++) {
        final focused = _focusedAmong(order);
        if (focused != null && (seen.isEmpty || seen.last != focused)) {
          seen.add(focused);
        }
        await tester.sendKeyEvent(LogicalKeyboardKey.tab);
        await tester.pump();
      }
      expect(seen, order);

      // Space switches the focused switch; Enter opens the focused button.
      Future<void> focusFromName(Key target) async {
        await tester.tap(_field('multiplayer-display-name'));
        await tester.pump();
        for (
          var press = 0;
          press < 10 && _focusedAmong(order) != target;
          press++
        ) {
          await tester.sendKeyEvent(LogicalKeyboardKey.tab);
          await tester.pump();
        }
        expect(_focusedAmong(order), target);
      }

      await focusFromName(const Key('multiplayer-upnp'));
      await tester.sendKeyEvent(LogicalKeyboardKey.space);
      await tester.pumpAndSettle();
      expect(gateway.calls, contains('change upnp true'));
      await focusFromName(const Key('read-disclosure'));
      await tester.sendKeyEvent(LogicalKeyboardKey.enter);
      await tester.pumpAndSettle();
      expect(find.text(MultiplayerPrivacyText.disclosureTitle), findsOneWidget);
      // Its one button has the focus: Enter acknowledges.
      await tester.sendKeyEvent(LogicalKeyboardKey.enter);
      await tester.pumpAndSettle();
      expect(gateway.calls, contains('change disclosure'));
    });

    testWidgets('Diagnostics → Network, by keyboard: every button in reading '
        'order, and the explanation never runs a check on Enter', (
      tester,
    ) async {
      final gateway = _FakeMultiplayerGateway();
      await _openNetwork(tester, gateway);
      const order = [
        Key('network-refresh'),
        Key('network-listen'),
        Key('network-check-ports'),
        Key('network-notice-again'),
      ];
      // Focuses a button without pressing it.
      Future<void> focus(Key button) async {
        Focus.of(
          tester.element(
            find.descendant(
              of: find.byKey(button),
              matching: find.byType(Text),
            ),
          ),
        ).requestFocus();
        await tester.pump();
      }

      await focus(order.first);
      final seen = <Key>[];
      for (var press = 0; press < 12 && seen.length < order.length; press++) {
        final focused = _focusedAmong(order);
        if (focused != null && (seen.isEmpty || seen.last != focused)) {
          seen.add(focused);
        }
        await tester.sendKeyEvent(LogicalKeyboardKey.tab);
        await tester.pump();
      }
      expect(seen, order);

      // On Check ports, Enter asks first, and Enter again cancels.
      await focus(order[2]);
      expect(_focusedAmong(order), order[2]);
      await tester.sendKeyEvent(LogicalKeyboardKey.enter);
      await tester.pumpAndSettle();
      expect(find.text(NetworkCheckNotice.title), findsOneWidget);
      await tester.sendKeyEvent(LogicalKeyboardKey.enter);
      await tester.pumpAndSettle();
      expect(find.text(NetworkCheckNotice.title), findsNothing);
      expect(gateway.calls, isNot(contains('check ports')));
    });
  });
}
