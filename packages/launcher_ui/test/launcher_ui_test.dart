import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:launcher_ui/launcher_ui.dart';

void main() {
  testWidgets('StatusPill renders label', (tester) async {
    await tester.pumpWidget(
      MaterialApp(
        theme: buildQuantumWorksTheme(),
        home: const Scaffold(
          body: StatusPill(label: 'Ready', tone: StatusTone.good),
        ),
      ),
    );

    expect(find.text('Ready'), findsOneWidget);
  });

  testWidgets('StatusPill exposes optional tooltip', (tester) async {
    await tester.pumpWidget(
      MaterialApp(
        theme: buildQuantumWorksTheme(),
        home: const Scaffold(
          body: StatusPill(
            label: 'Restart',
            tone: StatusTone.warning,
            tooltip: 'Relaunch Robotopia to apply pending changes.',
          ),
        ),
      ),
    );

    expect(
      find.byTooltip('Relaunch Robotopia to apply pending changes.'),
      findsOneWidget,
    );
  });
}
