part of 'join_flow_test.dart';

/// Where the join screens are rendered to, as PNG images with the
/// launcher's own fonts, for a person to look at. Unset, the render is
/// skipped: as with the multiplayer settings' renders, the images are
/// evidence for review, not compared in CI.
final _joinGoldenFolder = Platform.environment['TOPIAFORGE_GOLDEN_DIR'];

Future<void> _loadJoinFonts() async {
  const body = 'packages/launcher_ui/fonts/Quicksand-VariableFont_wght.ttf';
  for (final (family, asset) in [
    ('MaterialIcons', 'fonts/MaterialIcons-Regular.otf'),
    // Text styled without a family, as the theme's button labels are, is
    // drawn as boxes by the test renderer whatever is loaded here; on a
    // player's machine it is the system's font. The widget tests check
    // those labels' words.
    ('FlutterTest', body),
    ('packages/launcher_ui/TopiaForgeQuicksand', body),
    (
      'packages/launcher_ui/TopiaForgeAudiowide',
      'packages/launcher_ui/fonts/Audiowide-Regular.ttf',
    ),
  ]) {
    await (FontLoader(family)..addFont(rootBundle.load(asset))).load();
  }
}

/// Writes what is on screen, dialogs included, to `<folder>/<name>.png`.
Future<void> _renderJoin(WidgetTester tester, String name) async {
  await tester.pumpAndSettle();
  final screen = tester.element(find.byType(MaterialApp));
  await tester.runAsync(() async {
    final image = await captureImage(screen);
    final bytes = await image.toByteData(format: ui.ImageByteFormat.png);
    File(
      '$_joinGoldenFolder${Platform.pathSeparator}$name.png',
    ).writeAsBytesSync(bytes!.buffer.asUint8List());
  });
}

/// Takes the launcher off screen, so the next one starts with its own bloc.
Future<void> _freshJoinScene(WidgetTester tester) async {
  await tester.pumpWidget(const SizedBox());
  await tester.pump();
}

/// The consent sheet, then its failure card once Cancel is pressed.
Future<void> _renderSheetAndCard(WidgetTester tester, String theme) async {
  await _freshJoinScene(tester);
  final server = (await tester.runAsync(
    () => _server(passwordRequired: true),
  ))!;
  await _openApp(tester, gateway: _JoinGateway(server: server));
  await _toSheet(tester);
  await _renderJoin(tester, 'join-consent-$theme');
  await tester.tap(find.byKey(const Key('join-consent-cancel')));
  await _renderJoin(tester, 'join-failure-cancelled-$theme');
}

void registerJoinGoldenRenders() {
  testWidgets(
    'renders the join screens for review',
    skip: _joinGoldenFolder == null,
    (tester) async {
      await tester.runAsync(_loadJoinFonts);
      final crowded = (await tester.runAsync(_crowdedPlan))!;

      await _openApp(
        tester,
        gateway: _JoinGateway(
          preferences: MultiplayerPreferences(
            settings: MultiplayerSettings(displayName: 'Sam'),
          ),
        ),
      );
      _blocIn(
        tester,
      ).add(MultiplayerJoinRequested(JoinLinkTarget(_serverLink)));
      await _renderJoin(tester, 'join-disclosure');

      await _freshJoinScene(tester);
      await _openApp(tester);
      _blocIn(tester).add(
        MultiplayerJoinRequested(
          _publicAddress,
          origin: JoinFlowOrigin.address,
        ),
      );
      await _renderJoin(tester, 'join-direct-prompt');

      await _renderSheetAndCard(tester, 'light');

      await _freshJoinScene(tester);
      await _openApp(
        tester,
        gateway: _JoinGateway()..running = JoinGameRunning.owned,
      );
      await _toSheet(tester);
      await _renderJoin(tester, 'join-consent-closes-game');

      final hostServed = (await tester.runAsync(
        () => _server(hostServesPackages: true),
      ))!;
      // Without developer mode a mod only the host has blocks the plan;
      // with it, the sheet shows the mod and keeps Join off.
      for (final (developerMode, name) in [
        (false, 'join-failure-host-only'),
        (true, 'join-consent-host-only'),
      ]) {
        await _freshJoinScene(tester);
        await _openApp(
          tester,
          gateway: _JoinGateway(
            server: hostServed,
            plan: const JoinPlanner().plan(
              server: hostServed,
              resolutions: {
                _sha('zombies'): JoinPackageResolution.hostOffered(),
                _sha('sandbox'): JoinPackageResolution.installed(
                  _manifest('tf.sandbox', '1.4.0', name: 'Sandbox'),
                ),
              },
              environment: JoinEnvironment(
                gameBuild: _environment.gameBuild,
                runtimeVersion: _environment.runtimeVersion,
                sdkVersion: _environment.sdkVersion,
                protocolVersion: _environment.protocolVersion,
                developerMode: developerMode,
              ),
              mayContactHost: true,
            ),
          ),
        );
        _blocIn(tester).add(
          MultiplayerJoinRequested(
            JoinLinkTarget(
              SessionLink.direct(
                serverId: _serverId,
                host: '192.168.1.20',
                port: 27250,
              ),
            ),
          ),
        );
        await _renderJoin(tester, name);
      }

      await _freshJoinScene(tester);
      final downloading = _JoinGateway()
        ..hold(JoinStage.downloading)
        ..heldProgress[JoinStage.downloading] = JoinDownloadProgressed(
          bytesDone: 1300 * 1024,
          bytesTotal: 3 * 1024 * 1024,
        );
      await _openApp(tester, gateway: downloading);
      await _toSheet(tester);
      await tester.tap(find.byKey(const Key('join-consent-join')));
      await _renderJoin(tester, 'join-progress-downloading');

      await _freshJoinScene(tester);
      final (_, repository) = await _openApp(tester);
      await _toSheet(tester);
      await tester.tap(find.byKey(const Key('join-consent-join')));
      await tester.pumpAndSettle();
      repository.report(_activity(unconfirmed: true));
      await _renderJoin(tester, 'join-progress-waiting-for-game');
      repository.report(_activity(state: LaunchSessionState.connecting));
      await _renderJoin(tester, 'join-progress-connecting');
      repository.report(
        _activity(
          failure: LaunchMultiplayerFailure.timeout,
          detail: 'The server did not answer in time.',
        ),
      );
      await _renderJoin(tester, 'join-failure-timeout');

      await _freshJoinScene(tester);
      await _openApp(
        tester,
        gateway: _JoinGateway(
          server: crowded.$1,
          plan: const JoinPlanner().plan(
            server: crowded.$1,
            resolutions: const {},
            environment: _environment,
            mayContactHost: true,
          ),
        ),
      );
      _blocIn(
        tester,
      ).add(MultiplayerJoinRequested(JoinLinkTarget(_serverLink)));
      await _renderJoin(tester, 'join-failure-blockers');

      await _freshJoinScene(tester);
      await _openApp(tester, repository: _JoinRepository());
      _blocIn(
        tester,
      ).add(MultiplayerJoinRequested(JoinLinkTarget(_serverLink)));
      await _renderJoin(tester, 'join-problem-no-install');

      await _freshJoinScene(tester);
      await _openApp(tester);
      final bloc = _blocIn(tester);
      for (final host in ['192.168.1.20', 'sams-pc.lan', '2001:db8::20']) {
        bloc.add(
          MultiplayerJoinRequested(
            JoinAddressTarget(host: host, port: 27250),
            origin: JoinFlowOrigin.address,
          ),
        );
        await tester.pumpAndSettle();
      }
      for (var tries = 0; tries < 3 && _flowOf(bloc) != null; tries++) {
        bloc.add(MultiplayerJoinClosed(_flowOf(bloc)!.id));
        await tester.pumpAndSettle();
      }
      await tester.tap(find.byKey(const Key('join-by-address')));
      await _renderJoin(tester, 'join-direct-connect');

      tester.platformDispatcher.textScaleFactorTestValue = 2;
      await _freshJoinScene(tester);
      await _openApp(
        tester,
        size: const Size(800, 600),
        gateway: _JoinGateway(server: crowded.$1, plan: crowded.$2),
      );
      await _toSheet(tester);
      await _renderJoin(tester, 'join-consent-crowded-800x600-text2x');
      await tester.tap(find.byKey(const Key('join-consent-cancel')));
      await _renderJoin(tester, 'join-failure-800x600-text2x');
      tester.platformDispatcher.clearTextScaleFactorTestValue();

      tester.platformDispatcher.accessibilityFeaturesTestValue =
          const FakeAccessibilityFeatures(
            disableAnimations: true,
            highContrast: true,
          );
      await _renderSheetAndCard(tester, 'high-contrast');
      tester.platformDispatcher.accessibilityFeaturesTestValue =
          const FakeAccessibilityFeatures(disableAnimations: true);

      tester.platformDispatcher.platformBrightnessTestValue = Brightness.dark;
      await _renderSheetAndCard(tester, 'dark-system');
      tester.platformDispatcher.clearPlatformBrightnessTestValue();
    },
  );
}
