import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:launcher_domain/launcher_domain.dart';
import 'package:robotopia_launcher_flutter/src/launcher_app.dart';

part 'widget_test_fakes.dart';

// The Developer screen's ListView scrollable (stable across rebuilds). Scoped by key so it never resolves to a
// pane's internal TextField scrollable, which the lazy ListView disposes as content scrolls off-screen.
Finder _devScrollable() => find
    .descendant(
      of: find.byKey(const Key('developer-scroll')),
      matching: find.byType(Scrollable),
    )
    .first;

void main() {
  testWidgets('renders first-run launcher shell', (tester) async {
    await tester.pumpWidget(
      RobotopiaLauncherApp(repository: _FakeLauncherRepository()),
    );
    await tester.pumpAndSettle();

    expect(find.text('Library / Launch'), findsOneWidget);
    expect(find.text('Select Robotopia'), findsOneWidget);
    expect(find.text('Detect Install'), findsOneWidget);
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
    final repository = _FakeLauncherRepository(snapshot: _updateSnapshot());
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

    expect(repository.restartCount, 1);
    expect(find.text('Restarted Robotopia.'), findsOneWidget);
  });
}
