part of 'widget_test.dart';

/// Where the multiplayer screens are rendered to, as PNG images with the
/// launcher's own fonts, for a person to look at. Unset, the render is
/// skipped: the images are evidence for review, not compared in CI, since
/// text rasterises a little differently from one system to the next.
final _goldenFolder = Platform.environment['TOPIAFORGE_GOLDEN_DIR'];

Future<void> _loadLauncherFonts() async {
  const body = 'packages/launcher_ui/fonts/Quicksand-VariableFont_wght.ttf';
  for (final (family, asset) in [
    ('MaterialIcons', 'fonts/MaterialIcons-Regular.otf'),
    // Text styled without a family, as the theme's button labels are, is
    // drawn as boxes by the test renderer whatever is loaded here; on a
    // player's machine it is the system's font. The widget tests check
    // those labels' words.
    ('FlutterTest', body),
    (
      'packages/launcher_ui/TopiaForgeQuicksand',
      'packages/launcher_ui/fonts/Quicksand-VariableFont_wght.ttf',
    ),
    (
      'packages/launcher_ui/TopiaForgeAudiowide',
      'packages/launcher_ui/fonts/Audiowide-Regular.ttf',
    ),
  ]) {
    await (FontLoader(family)..addFont(rootBundle.load(asset))).load();
  }
}

/// Writes what is on screen, dialogs included, to `<folder>/<name>.png`.
Future<void> _render(WidgetTester tester, String name) async {
  await tester.pumpAndSettle();
  final screen = tester.element(find.byType(MaterialApp));
  await tester.runAsync(() async {
    final image = await captureImage(screen);
    final bytes = await image.toByteData(format: ui.ImageByteFormat.png);
    File(
      '$_goldenFolder${Platform.pathSeparator}$name.png',
    ).writeAsBytesSync(bytes!.buffer.asUint8List());
  });
}

/// Takes the launcher off screen, so the next one starts with its own bloc.
Future<void> _freshScene(WidgetTester tester) async {
  await tester.pumpWidget(const SizedBox());
  await tester.pump();
}

void registerMultiplayerGoldenRenders() {
  testWidgets(
    'renders the multiplayer screens for review',
    skip: _goldenFolder == null,
    (tester) async {
      await tester.runAsync(_loadLauncherFonts);

      await _openMultiplayerSettings(
        tester,
        _FakeMultiplayerGateway(),
        links: _FakeSessionLinkHandling(),
        size: const Size(1280, 1500),
      );
      await _render(tester, 'settings-multiplayer-editable');
      await tester.tap(find.byKey(const Key('read-disclosure')));
      await _render(tester, 'settings-multiplayer-disclosure');
      await tester.tap(find.text(MultiplayerPrivacyText.disclosureButton));
      await tester.pumpAndSettle();
      await tester.tap(find.byKey(const Key('voice-consent')));
      await _render(tester, 'settings-multiplayer-voice-consent');
      await tester.tap(find.text(MultiplayerPrivacyText.voiceOnButton));
      await tester.enterText(_field('multiplayer-display-name'), 'S');
      await _render(tester, 'settings-multiplayer-consented-name-too-short');

      await _freshScene(tester);
      await _openMultiplayerSettings(
        tester,
        _FakeMultiplayerGateway()..running = GameRunState.running,
        links: _FakeSessionLinkHandling(),
        size: const Size(1280, 1500),
      );
      await _render(tester, 'settings-multiplayer-read-only');

      await _freshScene(tester);
      await _openMultiplayerSettings(
        tester,
        _FakeMultiplayerGateway(
          preferences: MultiplayerPreferences(
            settings: MultiplayerSettings(displayName: 'Sam'),
            health: MultiplayerPreferencesHealth.reset,
          ),
        )..running = GameRunState.unknown,
        size: const Size(1280, 1500),
      );
      await _render(tester, 'settings-multiplayer-reset-cannot-tell');

      await _freshScene(tester);
      await _openMultiplayerSettings(
        tester,
        _FakeMultiplayerGateway()..unreadable = true,
        size: const Size(1280, 800),
      );
      await _render(tester, 'settings-multiplayer-unreadable');

      tester.platformDispatcher.textScaleFactorTestValue = 2;
      await _freshScene(tester);
      await _openMultiplayerSettings(
        tester,
        _FakeMultiplayerGateway()..running = GameRunState.running,
        size: const Size(800, 600),
      );
      await _render(tester, 'settings-multiplayer-800x600-text2x');
      await _freshScene(tester);
      await _openNetwork(
        tester,
        _FakeMultiplayerGateway()
          ..lan = LanBrowseSnapshot(
            status: LanDiscoveryStatus.listening,
            pending: 2,
            counters: const LanDiscoveryCounters(datagramsHeard: 41),
          ),
        size: const Size(800, 600),
      );
      await _render(tester, 'diagnostics-network-800x600-text2x');
      tester.platformDispatcher.clearTextScaleFactorTestValue();

      final network = _FakeMultiplayerGateway()
        ..lan = LanBrowseSnapshot(
          status: LanDiscoveryStatus.listening,
          pending: 1,
          beyondLocalNetwork: 1,
          counters: const LanDiscoveryCounters(
            datagramsHeard: 41,
            queriesSent: 7,
            repliesAccepted: 6,
            repliesRefused: 1,
          ),
        );
      await _freshScene(tester);
      await _openNetwork(tester, network, size: const Size(1280, 1100));
      await tester.tap(find.byKey(const Key('network-check-ports')));
      await _render(tester, 'diagnostics-network-notice');
      await tester.tap(find.text('Continue'));
      await _render(tester, 'diagnostics-network-ports-checked');
      network.failure = StateError('reset');
      await tester.tap(find.byKey(const Key('network-refresh')));
      await _render(tester, 'diagnostics-network-failed');

      tester.platformDispatcher.accessibilityFeaturesTestValue =
          const FakeAccessibilityFeatures(
            disableAnimations: true,
            highContrast: true,
          );
      await _freshScene(tester);
      await _openMultiplayerSettings(
        tester,
        _FakeMultiplayerGateway()..running = GameRunState.running,
        size: const Size(1280, 1500),
      );
      await _render(tester, 'settings-multiplayer-read-only-high-contrast');
      tester.platformDispatcher.accessibilityFeaturesTestValue =
          const FakeAccessibilityFeatures(disableAnimations: true);

      tester.platformDispatcher.platformBrightnessTestValue = Brightness.dark;
      await _freshScene(tester);
      await _openMultiplayerSettings(
        tester,
        _FakeMultiplayerGateway()..running = GameRunState.running,
        size: const Size(1280, 1500),
      );
      await _render(tester, 'settings-multiplayer-read-only-dark-system');
      tester.platformDispatcher.clearPlatformBrightnessTestValue();
    },
  );
}
