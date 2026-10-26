import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:launcher_ui/launcher_ui.dart';

Future<void> _pump(WidgetTester tester, Widget child) => tester.pumpWidget(
  MaterialApp(
    theme: buildTopiaForgeTheme(),
    home: Scaffold(body: SingleChildScrollView(child: child)),
  ),
);

void main() {
  Future<Future<bool>> openPrompt(WidgetTester tester) async {
    late Future<bool> result;
    await tester.pumpWidget(
      MaterialApp(
        theme: buildTopiaForgeTheme(),
        home: Scaffold(
          body: Builder(
            builder: (context) => TextButton(
              onPressed: () => result = showDirectConnectionPrompt(context),
              child: const Text('Open'),
            ),
          ),
        ),
      ),
    );
    await tester.tap(find.text('Open'));
    await tester.pumpAndSettle();
    return result;
  }

  testWidgets('the direct prompt is the approved text, with Cancel first', (
    tester,
  ) async {
    var result = await openPrompt(tester);
    expect(find.text(MultiplayerPrivacyText.directTitle), findsOneWidget);
    expect(find.text(MultiplayerPrivacyText.directBody), findsOneWidget);
    // Cancel is the primary button, and has the focus.
    final cancel = tester.widget<FilledButton>(
      find.widgetWithText(
        FilledButton,
        MultiplayerPrivacyText.directCancelButton,
      ),
    );
    expect(cancel.autofocus, isTrue);
    expect(
      find.widgetWithText(TextButton, MultiplayerPrivacyText.directAllowButton),
      findsOneWidget,
    );
    await tester.sendKeyEvent(LogicalKeyboardKey.enter);
    await tester.pumpAndSettle();
    expect(await result, isFalse, reason: 'Enter keeps Hide my IP on');

    result = await openPrompt(tester);
    await tester.sendKeyEvent(LogicalKeyboardKey.escape);
    await tester.pumpAndSettle();
    expect(await result, isFalse);

    result = await openPrompt(tester);
    await tester.tap(find.text(MultiplayerPrivacyText.directAllowButton));
    await tester.pumpAndSettle();
    expect(await result, isTrue);
  });

  testWidgets('the disclosure body is its three approved paragraphs', (
    tester,
  ) async {
    await _pump(tester, const PrivacyDisclosureBody());
    for (final text in [
      MultiplayerPrivacyText.disclosureExposure,
      MultiplayerPrivacyText.disclosureHidden,
      MultiplayerPrivacyText.disclosureChoice,
    ]) {
      expect(find.text(text), findsOneWidget);
    }
  });

  testWidgets('a step rail says where each step stands, in words', (
    tester,
  ) async {
    final semantics = tester.ensureSemantics();
    tester.platformDispatcher.accessibilityFeaturesTestValue =
        const FakeAccessibilityFeatures(disableAnimations: true);
    addTearDown(tester.platformDispatcher.clearAccessibilityFeaturesTestValue);
    await _pump(
      tester,
      const StepRail(
        steps: [
          StepRailItem(label: 'Checking', status: StepRailStatus.done),
          StepRailItem(label: 'Finding', status: StepRailStatus.skipped),
          StepRailItem(
            label: 'Downloading',
            status: StepRailStatus.current,
            detail: '1.0 MB of 4.0 MB',
            progress: 0.25,
          ),
          StepRailItem(label: 'Installing', status: StepRailStatus.pending),
          StepRailItem(label: 'Starting', status: StepRailStatus.failed),
        ],
      ),
    );
    expect(find.text('Finding (not needed)'), findsOneWidget);
    expect(find.text('1.0 MB of 4.0 MB'), findsOneWidget);
    expect(
      tester
          .widget<LinearProgressIndicator>(find.byType(LinearProgressIndicator))
          .value,
      0.25,
    );
    // With reduced motion the running step holds still.
    expect(find.byType(CircularProgressIndicator), findsNothing);
    expect(find.bySemanticsLabel('Checking, done'), findsOneWidget);
    expect(find.bySemanticsLabel('Finding, not needed'), findsOneWidget);
    expect(
      find.bySemanticsLabel(RegExp('^Downloading, in progress')),
      findsOne,
    );
    expect(find.bySemanticsLabel('Installing, waiting'), findsOneWidget);
    expect(find.bySemanticsLabel('Starting, stopped here'), findsOneWidget);
    semantics.dispose();
  });

  testWidgets('the running step spins while animations are on', (tester) async {
    await _pump(
      tester,
      const StepRail(
        steps: [StepRailItem(label: 'Joining', status: StepRailStatus.current)],
      ),
    );
    expect(find.byType(CircularProgressIndicator), findsOneWidget);
  });

  testWidgets('capability chips explain the canonical ones', (tester) async {
    await _pump(
      tester,
      const CapabilityChips(capabilities: ['hud', 'microphone', 'network']),
    );
    expect(find.text('microphone'), findsOneWidget);
    expect(find.text('hud'), findsOneWidget);
    expect(find.byTooltip(capabilityMeanings['microphone']!), findsOneWidget);
    expect(find.byTooltip(capabilityMeanings['network']!), findsOneWidget);

    await _pump(tester, const CapabilityChips(capabilities: []));
    expect(find.text('No capabilities declared.'), findsOneWidget);
  });

  testWidgets('a rail and chips fit a narrow window at large text', (
    tester,
  ) async {
    tester.view.physicalSize = const Size(320, 900);
    tester.view.devicePixelRatio = 1;
    tester.platformDispatcher.textScaleFactorTestValue = 2;
    addTearDown(() {
      tester.view.reset();
      tester.platformDispatcher.clearTextScaleFactorTestValue();
    });
    await _pump(
      tester,
      const Column(
        children: [
          StepRail(
            steps: [
              StepRailItem(
                label: "Saving the server's profile",
                status: StepRailStatus.current,
                detail: "Saving the server's profile, with every mod pinned.",
              ),
              StepRailItem(
                label: 'Receiving the world',
                status: StepRailStatus.pending,
              ),
            ],
          ),
          CapabilityChips(
            capabilities: [
              'speech-to-text',
              'remote-ai',
              'player-token',
              'unsafe-native',
            ],
          ),
        ],
      ),
    );
    expect(tester.takeException(), isNull);
  });
}
