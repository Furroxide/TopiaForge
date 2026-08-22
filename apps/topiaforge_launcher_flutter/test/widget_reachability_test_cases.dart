part of 'widget_test.dart';

/// A scripted probe gateway. Records every call so the tests can assert the negative case that matters most: a
/// probe that has not been switched on never runs.
class _FakeReachabilityProbe implements ReachabilityProbeGateway {
  _FakeReachabilityProbe({
    ReachabilityProbeSettings settings = const ReachabilityProbeSettings(),
    this.outcome = const ReachabilityProbeOutcome.completed(
      NatClassification(
        reachability: HostReachability.relayRequired,
        mapping: NatMappingBehavior.addressAndPortDependent,
        filtering: NatFilteringBehavior.addressAndPortDependent,
      ),
    ),
  }) : _settings = settings;

  ReachabilityProbeSettings _settings;
  final ReachabilityProbeOutcome outcome;

  final saved = <ReachabilityProbeSettings>[];
  int runCount = 0;

  @override
  Future<ReachabilityProbeSettings> loadSettings() async => _settings;

  @override
  Future<void> saveSettings(ReachabilityProbeSettings settings) async {
    _settings = settings;
    saved.add(settings);
  }

  @override
  Future<ReachabilityProbeOutcome> run({required bool developerMode}) async {
    runCount++;
    return outcome;
  }
}

/// Opens the Dev tab and scrolls the reachability pane into the built range. The Developer screen is a ListView,
/// so a pane below the fold is not built until it is scrolled to.
Future<void> _openReachabilityPane(WidgetTester tester) async {
  await tester.tap(find.text('Dev'));
  await tester.pumpAndSettle();
  await tester.scrollUntilVisible(
    find.text('Network reachability probe'),
    120,
    scrollable: _devScrollable(),
  );
  await tester.pumpAndSettle();
}

void registerReachabilityProbeWidgetTests() {
  testWidgets('the probe pane is hidden outside developer mode', (
    tester,
  ) async {
    await tester.pumpWidget(
      TopiaForgeLauncherApp(
        repository: _FakeLauncherRepository(),
        reachabilityProbe: _FakeReachabilityProbe(),
      ),
    );
    await tester.pumpAndSettle();

    expect(find.text('Dev'), findsNothing);
    expect(find.text('Network reachability probe'), findsNothing);
  });

  testWidgets('the probe reads as off and refuses to run until enabled', (
    tester,
  ) async {
    final probe = _FakeReachabilityProbe();
    await tester.pumpWidget(
      TopiaForgeLauncherApp(
        repository: _FakeLauncherRepository(developerMode: true),
        reachabilityProbe: probe,
      ),
    );
    await tester.pumpAndSettle();
    await _openReachabilityPane(tester);

    expect(find.text('Network reachability probe'), findsOneWidget);
    final pill = tester.widget<StatusPill>(
      find.byKey(const Key('reachability-state-pill')),
    );
    expect(pill.label, 'Off');

    final run = tester.widget<FilledButton>(
      find.byKey(const Key('reachability-run-button')),
    );
    expect(run.onPressed, isNull, reason: 'Run is disabled while off.');
    expect(probe.runCount, isZero);
  });

  testWidgets('sharing cannot be consented to while the probe is off', (
    tester,
  ) async {
    await tester.pumpWidget(
      TopiaForgeLauncherApp(
        repository: _FakeLauncherRepository(developerMode: true),
        reachabilityProbe: _FakeReachabilityProbe(),
      ),
    );
    await tester.pumpAndSettle();
    await _openReachabilityPane(tester);

    final sharing = tester.widget<SwitchListTile>(
      find.byKey(const Key('reachability-sharing-switch')),
    );
    expect(sharing.onChanged, isNull);
  });

  testWidgets('enabling the probe persists the opt-in', (tester) async {
    final probe = _FakeReachabilityProbe();
    await tester.pumpWidget(
      TopiaForgeLauncherApp(
        repository: _FakeLauncherRepository(developerMode: true),
        reachabilityProbe: probe,
      ),
    );
    await tester.pumpAndSettle();
    await _openReachabilityPane(tester);

    await tester.tap(find.byKey(const Key('reachability-enabled-switch')));
    await tester.pumpAndSettle();

    expect(probe.saved.single.enabled, isTrue);
    expect(probe.saved.single.shareAggregateResults, isFalse);
    expect(probe.runCount, isZero, reason: 'Opting in does not run the probe.');
  });

  testWidgets('turning the probe off withdraws sharing consent', (
    tester,
  ) async {
    final probe = _FakeReachabilityProbe(
      settings: const ReachabilityProbeSettings(
        enabled: true,
        shareAggregateResults: true,
      ),
    );
    await tester.pumpWidget(
      TopiaForgeLauncherApp(
        repository: _FakeLauncherRepository(developerMode: true),
        reachabilityProbe: probe,
      ),
    );
    await tester.pumpAndSettle();
    await _openReachabilityPane(tester);

    await tester.tap(find.byKey(const Key('reachability-enabled-switch')));
    await tester.pumpAndSettle();

    expect(probe.saved.single.enabled, isFalse);
    expect(
      probe.saved.single.shareAggregateResults,
      isFalse,
      reason: 'Re-enabling later must not silently restore old consent.',
    );
  });

  testWidgets('an enabled probe runs and shows its classification locally', (
    tester,
  ) async {
    final probe = _FakeReachabilityProbe(
      settings: const ReachabilityProbeSettings(enabled: true),
    );
    await tester.pumpWidget(
      TopiaForgeLauncherApp(
        repository: _FakeLauncherRepository(developerMode: true),
        reachabilityProbe: probe,
      ),
    );
    await tester.pumpAndSettle();
    await _openReachabilityPane(tester);

    await tester.tap(find.byKey(const Key('reachability-run-button')));
    await tester.pumpAndSettle();

    expect(probe.runCount, 1);
    final result = tester.widget<StatusPill>(
      find.byKey(const Key('reachability-result-pill')),
    );
    expect(result.label, 'Needs a relay');
    expect(result.tone, StatusTone.warning);
    expect(find.byKey(const Key('reachability-result-detail')), findsOneWidget);
  });

  testWidgets('a refused run reports why instead of failing silently', (
    tester,
  ) async {
    final probe = _FakeReachabilityProbe(
      settings: const ReachabilityProbeSettings(enabled: true),
      outcome: const ReachabilityProbeOutcome.refused(
        ReachabilityProbeRefusal.privacyNoticeNotApproved,
      ),
    );
    await tester.pumpWidget(
      TopiaForgeLauncherApp(
        repository: _FakeLauncherRepository(developerMode: true),
        reachabilityProbe: probe,
      ),
    );
    await tester.pumpAndSettle();
    await _openReachabilityPane(tester);

    await tester.tap(find.byKey(const Key('reachability-run-button')));
    await tester.pumpAndSettle();

    expect(find.textContaining('No approved privacy notice'), findsOneWidget);
  });

  testWidgets('the launcher works with no probe wired at all', (tester) async {
    await tester.pumpWidget(
      TopiaForgeLauncherApp(
        repository: _FakeLauncherRepository(developerMode: true),
      ),
    );
    await tester.pumpAndSettle();
    await _openReachabilityPane(tester);

    expect(find.text('Network reachability probe'), findsOneWidget);
    await tester.tap(find.byKey(const Key('reachability-enabled-switch')));
    await tester.pumpAndSettle();

    expect(
      find.textContaining('reachability probe is unavailable'),
      findsOneWidget,
    );
  });
}
