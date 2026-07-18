part of 'widget_test.dart';

void registerAccessibilityWidgetTests() {
  testWidgets('pixel wordmark fits the compact top bar at enlarged text', (
    tester,
  ) async {
    tester.view.physicalSize = const Size(700, 600);
    tester.view.devicePixelRatio = 1;
    tester.platformDispatcher.textScaleFactorTestValue = 1.4;
    addTearDown(() {
      tester.view.reset();
      tester.platformDispatcher.clearTextScaleFactorTestValue();
    });

    await tester.pumpWidget(
      TopiaForgeLauncherApp(
        repository: _FakeLauncherRepository(snapshot: _readySnapshot()),
      ),
    );
    await tester.pumpAndSettle();

    final logos = tester.widgetList<TopiaForgeLogo>(
      find.byType(TopiaForgeLogo),
    );
    expect(logos, isNotEmpty);
    expect(logos.first.height, 36);
    expect(tester.takeException(), isNull);
  });

  testWidgets('shell supports high contrast and 200 percent text scaling', (
    tester,
  ) async {
    tester.view.physicalSize = const Size(800, 600);
    tester.view.devicePixelRatio = 1;
    tester.platformDispatcher.textScaleFactorTestValue = 2;
    tester.platformDispatcher.accessibilityFeaturesTestValue =
        const FakeAccessibilityFeatures(
          disableAnimations: true,
          highContrast: true,
          reduceMotion: true,
        );
    addTearDown(() {
      tester.view.reset();
      tester.platformDispatcher.clearTextScaleFactorTestValue();
      tester.platformDispatcher.clearAccessibilityFeaturesTestValue();
    });

    await tester.pumpWidget(
      TopiaForgeLauncherApp(
        repository: _FakeLauncherRepository(snapshot: _readySnapshot()),
      ),
    );
    await tester.pumpAndSettle();

    final rail = tester.widget<NavigationRail>(find.byType(NavigationRail));
    expect(rail.labelType, NavigationRailLabelType.none);
    expect(
      Theme.of(tester.element(find.byType(LauncherShell))).colorScheme.outline,
      TopiaForgePalette.darkPanel,
    );
    expect(
      find.byWidgetPredicate(
        (widget) => widget is Semantics && widget.properties.liveRegion == true,
      ),
      findsWidgets,
    );
    expect(tester.takeException(), isNull);
  });

  testWidgets('keyboard navigation restores focus after section activation', (
    tester,
  ) async {
    await tester.pumpWidget(
      TopiaForgeLauncherApp(
        repository: _FakeLauncherRepository(snapshot: _readySnapshot()),
      ),
    );
    await tester.pumpAndSettle();

    await tester.sendKeyDownEvent(LogicalKeyboardKey.controlLeft);
    await tester.sendKeyEvent(LogicalKeyboardKey.digit3);
    await tester.sendKeyUpEvent(LogicalKeyboardKey.controlLeft);
    await tester.pumpAndSettle();

    expect(
      tester.widget<LauncherBody>(find.byType(LauncherBody)).state.section,
      LauncherSection.mods,
    );
    final focus = tester.widget<Focus>(
      find.byKey(const ValueKey('launcher-shell-focus')),
    );
    expect(focus.focusNode?.hasFocus, isTrue);

    await tester.sendKeyDownEvent(LogicalKeyboardKey.altLeft);
    await tester.sendKeyEvent(LogicalKeyboardKey.arrowRight);
    await tester.sendKeyUpEvent(LogicalKeyboardKey.altLeft);
    await tester.pumpAndSettle();
    expect(
      tester.widget<LauncherBody>(find.byType(LauncherBody)).state.section,
      LauncherSection.browse,
    );
  });
}
