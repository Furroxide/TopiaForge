import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:launcher_domain/launcher_domain.dart';
import 'package:launcher_ui/launcher_ui.dart';
import 'package:robotopia_launcher_flutter/src/launcher_app.dart';

part 'widget_test_fakes.dart';
part 'widget_test_snapshots.dart';

// The Developer screen's ListView scrollable (stable across rebuilds). Scoped by key so it never resolves to a
// pane's internal TextField scrollable, which the lazy ListView disposes as content scrolls off-screen.
Finder _devScrollable() => find
    .descendant(
      of: find.byKey(const Key('developer-scroll')),
      matching: find.byType(Scrollable),
    )
    .first;

void main() {
  final binding = TestWidgetsFlutterBinding.ensureInitialized();

  // Home's GlowButton pulses on a repeating AnimationController, which would
  // deadlock pumpAndSettle. Running the suite with reduced motion keeps every
  // pumpAndSettle finite and permanently exercises the reduced-motion path.
  setUp(() {
    binding.platformDispatcher.accessibilityFeaturesTestValue =
        const FakeAccessibilityFeatures(disableAnimations: true);
  });
  tearDown(() {
    binding.platformDispatcher.clearAccessibilityFeaturesTestValue();
  });

  testWidgets('renders first-run welcome hero', (tester) async {
    await tester.pumpWidget(
      RobotopiaLauncherApp(repository: _FakeLauncherRepository()),
    );
    await tester.pumpAndSettle();

    expect(find.text('Welcome to Robotopia modding'), findsOneWidget);
    // GlowButton renders its label uppercased.
    expect(find.text('FIND MY GAME'), findsOneWidget);
    expect(find.text('Choose the folder myself'), findsOneWidget);
    expect(find.text('Pick your mods'), findsOneWidget);
    expect(
      find.descendant(
        of: find.byType(NavigationRail),
        matching: find.byType(Badge),
      ),
      findsNothing,
    );
  });

  // Home stacks the hero, profiles, and discover zones vertically; the
  // default 800x600 test window clips the lower zones, so home tests run in a
  // taller viewport to keep every target tappable.
  Future<void> pumpHome(
    WidgetTester tester,
    _FakeLauncherRepository repository,
  ) async {
    tester.view.physicalSize = const Size(1280, 2200);
    tester.view.devicePixelRatio = 1.0;
    addTearDown(tester.view.reset);
    await tester.pumpWidget(RobotopiaLauncherApp(repository: repository));
    await tester.pumpAndSettle();
  }

  testWidgets('home launch pad renders ready state and update pill', (
    tester,
  ) async {
    await pumpHome(
      tester,
      _FakeLauncherRepository(snapshot: _updateSnapshot()),
    );

    expect(find.text('Ready for liftoff'), findsOneWidget);
    expect(find.text('Game found'), findsOneWidget);
    // Home's systems check and the global status bar both report the runtime.
    expect(find.text('Runtime ready'), findsWidgets);
    expect(find.text('1 mod enabled'), findsOneWidget);

    // The updates pill deep-links into Browse.
    await tester.tap(find.text('1 update available'));
    await tester.pumpAndSettle();
    expect(find.text('Preview Update'), findsOneWidget);
  });

  testWidgets('sidebar badges Mods and Browse when mod updates exist', (
    tester,
  ) async {
    await tester.pumpWidget(
      RobotopiaLauncherApp(
        repository: _FakeLauncherRepository(snapshot: _updateSnapshot()),
      ),
    );
    await tester.pumpAndSettle();

    final rail = find.byType(NavigationRail);
    expect(
      find.descendant(of: rail, matching: find.byType(Badge)),
      findsNWidgets(2),
    );
    expect(
      find.descendant(of: rail, matching: find.text('1')),
      findsNWidgets(2),
    );
  });

  testWidgets('home glow button launches the selected profile', (tester) async {
    final repository = _FakeLauncherRepository(snapshot: _updateSnapshot());
    await pumpHome(tester, repository);

    await tester.tap(find.text('LAUNCH'));
    await tester.pumpAndSettle();

    expect(repository.launchedProfileIds, ['default']);
    expect(find.text('Launched Robotopia.'), findsOneWidget);
  });

  testWidgets('home shows almost-ready state and one-click runtime fix', (
    tester,
  ) async {
    final repository = _FakeLauncherRepository(
      snapshot: _readySnapshot(needsRepair: true),
    );
    await pumpHome(tester, repository);

    expect(find.text('Almost ready'), findsOneWidget);
    final glowButton = tester.widget<GlowButton>(find.byType(GlowButton));
    expect(glowButton.onPressed, isNotNull);

    await tester.tap(find.text('Runtime needs a quick fix'));
    await tester.pumpAndSettle();
    expect(repository.installOrRepairRuntimeCount, 1);
  });

  testWidgets('launch auto-repairs stale runtime before starting', (
    tester,
  ) async {
    final repository = _FakeLauncherRepository(
      snapshot: _readySnapshot(needsRepair: true),
    );
    await pumpHome(tester, repository);

    await tester.tap(find.text('LAUNCH'));
    await tester.pumpAndSettle();

    expect(repository.installOrRepairRuntimeCount, 1);
    expect(repository.launchedProfileIds, ['default']);
    expect(find.text('Launched Robotopia.'), findsOneWidget);
  });

  testWidgets('bottom repair-needed chip runs runtime repair', (tester) async {
    final repository = _FakeLauncherRepository(
      snapshot: _readySnapshot(needsRepair: true),
    );
    await pumpHome(tester, repository);

    await tester.tap(find.text('Repair needed'));
    await tester.pumpAndSettle();

    expect(repository.installOrRepairRuntimeCount, 1);
    expect(find.text('Runtime ready'), findsWidgets);
  });

  testWidgets('bottom no-game chip opens Setup', (tester) async {
    await tester.pumpWidget(
      RobotopiaLauncherApp(repository: _FakeLauncherRepository()),
    );
    await tester.pumpAndSettle();

    await tester.tap(find.text('No game selected'));
    await tester.pumpAndSettle();

    expect(find.text('Select Robotopia'), findsOneWidget);
    expect(find.text('Detect Install'), findsOneWidget);
  });

  testWidgets('home discover rail funnels into Browse when registry is empty', (
    tester,
  ) async {
    await pumpHome(tester, _FakeLauncherRepository(snapshot: _readySnapshot()));

    expect(find.text('Find your first mod'), findsOneWidget);

    await tester.tap(find.text('Open Browse'));
    await tester.pumpAndSettle();
    expect(find.text('No local packages'), findsOneWidget);
  });

  testWidgets('home discovery rail moves framework mods below regular mods', (
    tester,
  ) async {
    await pumpHome(
      tester,
      _FakeLauncherRepository(snapshot: _discoverySnapshot()),
    );

    expect(find.text('Gameplay Mod'), findsOneWidget);
    expect(find.text('Framework Mod'), findsOneWidget);
    expect(
      tester.getTopLeft(find.text('Gameplay Mod')).dx,
      lessThan(tester.getTopLeft(find.text('Framework Mod')).dx),
    );
  });

  testWidgets(
    'browse moves framework mods below regular mods for non-dev users',
    (tester) async {
      await tester.pumpWidget(
        RobotopiaLauncherApp(
          repository: _FakeLauncherRepository(snapshot: _discoverySnapshot()),
        ),
      );
      await tester.pumpAndSettle();

      await tester.tap(find.text('Browse'));
      await tester.pumpAndSettle();

      expect(find.text('Gameplay Mod'), findsOneWidget);
      expect(find.text('Framework Mod'), findsOneWidget);
      expect(
        tester.getTopLeft(find.text('Gameplay Mod')).dy,
        lessThan(tester.getTopLeft(find.text('Framework Mod')).dy),
      );
    },
  );

  testWidgets('browse preserves registry order for dev-mode users', (
    tester,
  ) async {
    await tester.pumpWidget(
      RobotopiaLauncherApp(
        repository: _FakeLauncherRepository(
          snapshot: _discoverySnapshot(developerMode: true),
        ),
      ),
    );
    await tester.pumpAndSettle();

    await tester.tap(find.text('Browse'));
    await tester.pumpAndSettle();

    expect(find.text('Framework Mod'), findsOneWidget);
    expect(find.text('Gameplay Mod'), findsOneWidget);
    expect(
      tester.getTopLeft(find.text('Framework Mod')).dy,
      lessThan(tester.getTopLeft(find.text('Gameplay Mod')).dy),
    );
  });

  testWidgets('profile card play button launches that profile', (tester) async {
    final repository = _FakeLauncherRepository(
      snapshot: _readySnapshot(
        profiles: [
          LauncherProfile.defaultProfile(),
          const LauncherProfile(id: 'coop', name: 'Co-op'),
        ],
      ),
    );
    await pumpHome(tester, repository);

    expect(find.text('Jump back in'), findsOneWidget);
    expect(find.text('Play'), findsNWidgets(2));
    // Selected profile is listed first, so the last Play belongs to Co-op.
    await tester.tap(find.text('Play').last);
    await tester.pumpAndSettle();

    expect(repository.launchedProfileIds, ['coop']);
  });

  testWidgets('profile card play auto-repairs before launch', (tester) async {
    final repository = _FakeLauncherRepository(
      snapshot: _readySnapshot(
        needsRepair: true,
        profiles: [
          LauncherProfile.defaultProfile(),
          const LauncherProfile(id: 'coop', name: 'Co-op'),
        ],
      ),
    );
    await pumpHome(tester, repository);

    await tester.tap(find.text('Play').last);
    await tester.pumpAndSettle();

    expect(repository.installOrRepairRuntimeCount, 1);
    expect(repository.launchedProfileIds, ['coop']);
  });

  testWidgets('setup screen keeps launch configuration', (tester) async {
    await tester.pumpWidget(
      RobotopiaLauncherApp(
        repository: _FakeLauncherRepository(snapshot: _updateSnapshot()),
      ),
    );
    await tester.pumpAndSettle();

    await tester.tap(find.text('Setup'));
    await tester.pumpAndSettle();

    expect(find.text('Load Order'), findsOneWidget);
    expect(find.text('Repair Runtime'), findsOneWidget);
    expect(find.text('World'), findsWidgets);
  });

  testWidgets('glow button pulses when animations are enabled', (tester) async {
    // Override the suite-wide reduced-motion default for this test only.
    binding.platformDispatcher.accessibilityFeaturesTestValue =
        const FakeAccessibilityFeatures();

    await tester.pumpWidget(
      RobotopiaLauncherApp(
        repository: _FakeLauncherRepository(snapshot: _updateSnapshot()),
      ),
    );
    // Never pumpAndSettle here: the glow repeats forever. Fixed frames only.
    await tester.pump(const Duration(milliseconds: 100));
    await tester.pump(const Duration(milliseconds: 400));
    await tester.pump(const Duration(milliseconds: 400));

    expect(find.byType(GlowButton), findsOneWidget);
    expect(tester.hasRunningAnimations, isTrue);
  });

  testWidgets('developer tab is hidden until developer mode is enabled', (
    tester,
  ) async {
    await tester.pumpWidget(
      RobotopiaLauncherApp(
        repository: _FakeLauncherRepository(),
        developerRepository: _FakeDeveloperRepository(),
      ),
    );
    await tester.pumpAndSettle();

    // Consumer default: no Developer tab.
    expect(find.text('Dev'), findsNothing);

    // Enable it from Settings.
    await tester.tap(find.text('Settings'));
    await tester.pumpAndSettle();
    final toggle = find.widgetWithText(SwitchListTile, 'Developer mode');
    await tester.ensureVisible(toggle);
    await tester.tap(toggle);
    await tester.pumpAndSettle();

    expect(find.text('Dev'), findsOneWidget);
  });

  testWidgets('renders developer workspace status', (tester) async {
    await tester.pumpWidget(
      RobotopiaLauncherApp(
        repository: _FakeLauncherRepository(developerMode: true),
        developerRepository: _FakeDeveloperRepository(),
      ),
    );
    await tester.pumpAndSettle();

    await tester.tap(find.text('Dev'));
    await tester
        .pumpAndSettle(); // auto-loads the workspace + environment + projects

    expect(find.text('Developer'), findsOneWidget);

    // The VCC-style Projects pane now sits above the project panes; scroll each target into the built range.
    final scrollable = _devScrollable();
    await tester.scrollUntilVisible(
      find.text('Creator Mod'),
      120,
      scrollable: scrollable,
    );
    expect(find.text('Creator Mod'), findsOneWidget);
    await tester.scrollUntilVisible(
      find.text('Resolve / Restore'),
      120,
      scrollable: scrollable,
    );
    expect(find.text('Resolve / Restore'), findsOneWidget);

    await tester.scrollUntilVisible(
      find.text('api.mod 1.0.0: ref/Api.dll'),
      160,
      scrollable: scrollable,
    );
    expect(find.text('api.mod 1.0.0: ref/Api.dll'), findsOneWidget);
  });

  testWidgets('developer environment pane shows tool status', (tester) async {
    await tester.pumpWidget(
      RobotopiaLauncherApp(
        repository: _FakeLauncherRepository(developerMode: true),
        developerRepository: _FakeDeveloperRepository(),
      ),
    );
    await tester.pumpAndSettle();

    await tester.tap(find.text('Dev'));
    await tester.pumpAndSettle(); // environment auto-populates on Dev open

    // The Environment pane sits below the Project pane; scroll it into the built range.
    await tester.scrollUntilVisible(
      find.text('Environment'),
      200,
      scrollable: _devScrollable(),
    );
    expect(find.text('Environment'), findsOneWidget);
    expect(find.text('Toolchain ready'), findsOneWidget);
    expect(find.text('.NET SDK — v8.0.100'), findsOneWidget);
  });

  testWidgets('setup button runs runSetup', (tester) async {
    final developer = _FakeDeveloperRepository();
    await tester.pumpWidget(
      RobotopiaLauncherApp(
        repository: _FakeLauncherRepository(developerMode: true),
        developerRepository: developer,
      ),
    );
    await tester.pumpAndSettle();

    await tester.tap(find.text('Dev'));
    await tester.pumpAndSettle();

    final setupButton = find.widgetWithText(FilledButton, 'Setup / Auto-fix');
    await tester.scrollUntilVisible(
      setupButton,
      120,
      scrollable: _devScrollable(),
    );
    await tester.ensureVisible(setupButton);
    await tester.pumpAndSettle();
    await tester.tap(setupButton);
    await tester.pumpAndSettle();

    expect(developer.runSetupCount, 1);
    expect(
      find.textContaining('sidecar dependencies already present'),
      findsWidgets,
    );
  });

  testWidgets('shows available updates for installed mods', (tester) async {
    await tester.pumpWidget(
      RobotopiaLauncherApp(
        repository: _FakeLauncherRepository(snapshot: _updateSnapshot()),
      ),
    );
    await tester.pumpAndSettle();

    await tester.tap(find.text('Mods'));
    await tester.pumpAndSettle();

    expect(find.text('Update'), findsWidgets);
    expect(find.text('Update available'), findsOneWidget);
    expect(
      find.byTooltip(
        'Robotopia must be relaunched before this pending mod change is applied to the running game. This clears after the loader starts with the current mod state.',
      ),
      findsWidgets,
    );
    await tester.scrollUntilVisible(
      find.text('Preview Update'),
      160,
      scrollable: find.byType(Scrollable).last,
    );
    expect(find.text('Preview Update'), findsOneWidget);

    await tester.tap(find.text('Browse'));
    await tester.pumpAndSettle();

    expect(find.text('Update'), findsOneWidget);
    expect(find.text('Preview Update'), findsOneWidget);
  });

  testWidgets('confirms restart before relaunching Robotopia', (tester) async {
    final repository = _FakeLauncherRepository(
      snapshot: _updateSnapshot(needsRepair: true),
    );
    await tester.pumpWidget(RobotopiaLauncherApp(repository: repository));
    await tester.pumpAndSettle();

    await tester.tap(find.text('Mods'));
    await tester.pumpAndSettle();

    await tester.tap(find.text('Restart').first);
    await tester.pumpAndSettle();

    expect(find.text('Restart Robotopia?'), findsOneWidget);
    expect(repository.restartCount, 0);

    await tester.tap(find.widgetWithText(FilledButton, 'Restart'));
    await tester.pumpAndSettle();

    expect(repository.installOrRepairRuntimeCount, 1);
    expect(repository.restartCount, 1);
    expect(find.text('Restarted Robotopia.'), findsOneWidget);
  });
}
