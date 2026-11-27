part of 'join_flow_test.dart';

/// A server with many mods, long names and every canonical capability, so a
/// sheet has the most to fit.
Future<(VerifiedServerDescriptor, JoinPlan)> _crowdedPlan() async {
  final packages = [
    for (var index = 0; index < 12; index++)
      DescriptorPackage(
        id: 'tf.community.extended-mod-number-$index',
        version: '1.$index.0-preview.$index',
        sha256: _sha('crowded $index'),
        role: DescriptorPackageRole.implicit,
      ),
  ];
  final server = await _server(
    name: 'The Very Long Named Community Server Of Sam',
    packages: packages,
    passwordRequired: true,
  );
  final plan = const JoinPlanner().plan(
    server: server,
    resolutions: {
      for (var index = 0; index < packages.length; index++)
        packages[index].sha256: index.isEven
            ? JoinPackageResolution.available(
                manifest: _manifest(
                  packages[index].id,
                  packages[index].version,
                  name: 'Extended Community Mod With A Long Name $index',
                  capabilities: [
                    'network',
                    'remote-ai',
                    'player-token',
                    'microphone',
                    'speech-to-text',
                    'unsafe-native',
                    'hud',
                  ],
                ),
                sourceId: 'community',
                sourceName: 'A Community Mod Source With A Long Name',
                downloadUrl: 'https://packages.example/$index.topiaforgemod',
                sizeBytes: (index + 1) * 900 * 1024,
              )
            : JoinPackageResolution.installed(
                _manifest(packages[index].id, packages[index].version),
              ),
    },
    environment: _environment,
    mayContactHost: true,
  );
  return (server, plan);
}

/// Each screen of the join dialog, set up on a gateway and reached.
Map<String, Future<void> Function(WidgetTester tester, Size size)>
_joinScreens() => {
  'the disclosure': (tester, size) async {
    await _openApp(
      tester,
      size: size,
      gateway: _JoinGateway(
        preferences: MultiplayerPreferences(settings: MultiplayerSettings()),
      ),
    );
    _blocIn(tester).add(MultiplayerJoinRequested(JoinLinkTarget(_serverLink)));
    await tester.pumpAndSettle();
  },
  'the direct prompt': (tester, size) async {
    await _openApp(tester, size: size);
    _blocIn(tester).add(
      MultiplayerJoinRequested(_publicAddress, origin: JoinFlowOrigin.address),
    );
    await tester.pumpAndSettle();
  },
  'a crowded consent sheet': (tester, size) async {
    final (server, plan) = (await tester.runAsync(_crowdedPlan))!;
    await _openApp(
      tester,
      size: size,
      gateway: _JoinGateway(server: server, plan: plan)
        ..running = JoinGameRunning.owned,
    );
    _blocIn(tester).add(MultiplayerJoinRequested(JoinLinkTarget(_serverLink)));
    await tester.pumpAndSettle();
  },
  'a consent sheet with a mod only the host has': (tester, size) async {
    await _toHostServedSheet(tester, size: size, capabilities: ['network']);
  },
  'the progress rail': (tester, size) async {
    final gateway = _JoinGateway()..hold(JoinStage.installing);
    await _openApp(tester, size: size, gateway: gateway);
    await _toSheet(tester);
    await tester.tap(find.byKey(const Key('join-consent-join')));
    await tester.pumpAndSettle();
  },
  'a failure card with blockers': (tester, size) async {
    final (server, plan) = (await tester.runAsync(_crowdedPlan))!;
    final blocked = const JoinPlanner().plan(
      server: server,
      resolutions: const {},
      environment: _environment,
      mayContactHost: true,
    );
    expect(blocked.isJoinable, isFalse);
    expect(plan.isJoinable, isTrue);
    await _openApp(
      tester,
      size: size,
      gateway: _JoinGateway(server: server, plan: blocked),
    );
    _blocIn(tester).add(MultiplayerJoinRequested(JoinLinkTarget(_serverLink)));
    await tester.pumpAndSettle();
  },
  'a stop before the join began': (tester, size) async {
    await _openApp(tester, size: size, repository: _JoinRepository());
    _blocIn(tester).add(MultiplayerJoinRequested(JoinLinkTarget(_serverLink)));
    await tester.pumpAndSettle();
  },
  'join by address with its history': (tester, size) async {
    await _openApp(tester, size: size);
    final bloc = _blocIn(tester);
    // Each typed address replaces the last one's question, and the dialog
    // keeps the most recent few.
    for (var index = 0; index < 6; index++) {
      bloc.add(
        MultiplayerJoinRequested(
          JoinAddressTarget(host: '192.168.1.${20 + index}', port: 27250),
          origin: JoinFlowOrigin.address,
        ),
      );
      await tester.pumpAndSettle();
    }
    for (var tries = 0; tries < 3 && _flowOf(bloc) != null; tries++) {
      bloc.add(MultiplayerJoinClosed(_flowOf(bloc)!.id));
      await tester.pumpAndSettle();
    }
    expect(_flowOf(bloc), isNull);
    expect(bloc.state.multiplayer.join.typedAddresses, hasLength(6));
    await tester.tap(find.byKey(const Key('join-by-address')));
    await tester.pumpAndSettle();
    expect(find.byKey(const Key('direct-connect-history-5')), findsOneWidget);
  },
};

void registerJoinLayoutTests() {
  group('The join dialog keeps its layout', () {
    for (final (size, scale) in [
      (const Size(800, 600), 1.0),
      (const Size(800, 600), 2.0),
      (const Size(1280, 720), 1.0),
    ]) {
      for (final MapEntry(key: name, value: open) in _joinScreens().entries) {
        testWidgets('$name at $size, text x$scale', (tester) async {
          tester.platformDispatcher.textScaleFactorTestValue = scale;
          addTearDown(tester.platformDispatcher.clearTextScaleFactorTestValue);
          await open(tester, size);
          expect(tester.takeException(), isNull);
          final scroll = find.byKey(const Key('join-scroll'));
          if (scroll.evaluate().isNotEmpty) {
            await tester.drag(scroll, const Offset(0, -6000));
            await tester.pumpAndSettle();
          }
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
      testWidgets('the sheet and a card render in the $theme theme, readable', (
        tester,
      ) async {
        tester.platformDispatcher
          ..accessibilityFeaturesTestValue = features
          ..platformBrightnessTestValue = brightness;
        addTearDown(tester.platformDispatcher.clearPlatformBrightnessTestValue);
        await _openApp(tester);
        await _toSheet(tester);
        final theme = Theme.of(
          tester.element(find.byKey(const Key('join-consent-sheet'))),
        );
        // The launcher has a light theme and a high-contrast one; a dark
        // system keeps the light one.
        expect(theme.brightness, Brightness.light);
        expect(
          theme.colorScheme.primary,
          features.highContrast
              ? TopiaForgePalette.darkPanel
              : TopiaForgePalette.launch,
        );
        expect(tester.takeException(), isNull);
        await tester.tap(find.byKey(const Key('join-consent-cancel')));
        await tester.pumpAndSettle();
        expect(find.byKey(const Key('join-failure-card')), findsOneWidget);
        // Every colour the dialog writes text or icons in stays readable on
        // its surface.
        final surface = theme.dialogTheme.backgroundColor!;
        for (final (color, minimum) in [
          (TopiaForgePalette.text, 4.5),
          (TopiaForgePalette.mutedText, 4.5),
          (TopiaForgePalette.danger, 3.0),
          (TopiaForgePalette.launchDark, 3.0),
          (TopiaForgePalette.good, 3.0),
          (TopiaForgePalette.accentDark, 3.0),
        ]) {
          expect(_contrast(color, surface), greaterThanOrEqualTo(minimum));
        }
        expect(
          _contrast(TopiaForgePalette.text, TopiaForgePalette.surfaceAlt),
          greaterThanOrEqualTo(4.5),
        );
      });
    }

    testWidgets("the keyboard moves through the sheet's buttons, Cancel "
        'first, and Space on Join consents', (tester) async {
      await _openApp(tester);
      await _toSheet(tester);
      expect(_focused(const Key('join-consent-cancel')), isTrue);
      await tester.sendKeyEvent(LogicalKeyboardKey.tab);
      await tester.pump();
      expect(_focused(const Key('join-consent-join')), isTrue);
      await tester.sendKeyDownEvent(LogicalKeyboardKey.shift);
      await tester.sendKeyEvent(LogicalKeyboardKey.tab);
      await tester.sendKeyUpEvent(LogicalKeyboardKey.shift);
      await tester.pump();
      expect(_focused(const Key('join-consent-cancel')), isTrue);
      await tester.sendKeyEvent(LogicalKeyboardKey.tab);
      await tester.sendKeyEvent(LogicalKeyboardKey.space);
      await tester.pumpAndSettle();
      expect(find.byKey(const Key('join-consent-sheet')), findsNothing);
      expect(
        _flowOf(_blocIn(tester))!.join!.stage.index,
        greaterThan(JoinStage.awaitingConsent.index),
      );
    });

    testWidgets("the keyboard moves between the direct prompt's two "
        'buttons, Cancel first', (tester) async {
      await _openApp(tester);
      _blocIn(tester).add(
        MultiplayerJoinRequested(
          _publicAddress,
          origin: JoinFlowOrigin.address,
        ),
      );
      await tester.pumpAndSettle();
      expect(_focused(const Key('direct-connection-cancel')), isTrue);
      await tester.sendKeyEvent(LogicalKeyboardKey.tab);
      await tester.pump();
      expect(_focused(const Key('direct-connection-allow')), isTrue);
      await tester.sendKeyEvent(LogicalKeyboardKey.tab);
      await tester.pump();
      expect(_focused(const Key('direct-connection-cancel')), isTrue);
    });
  });
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
