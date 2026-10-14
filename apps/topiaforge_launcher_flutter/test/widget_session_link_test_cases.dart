part of 'widget_test.dart';

/// Scripted link handling that records every call. A change of the setting
/// reports its registration on [statuses], as the real one does.
final class _FakeSessionLinkHandling implements SessionLinkHandlingGateway {
  bool enabled = true;
  final calls = <String>[];
  final _statuses = StreamController<SessionLinkHandlingStatus>.broadcast();
  Object? saveError;

  @override
  Stream<SessionLinkHandlingStatus> get statuses => _statuses.stream;

  @override
  Future<SessionLinkHandlingStatus> start() async {
    calls.add('start');
    return SessionLinkHandlingStatus(
      enabled: enabled,
      registration: enabled ? SessionLinkRegistration.registered : null,
    );
  }

  @override
  Future<void> setEnabled(bool enabled) async {
    calls.add('set $enabled');
    final error = saveError;
    if (error != null) throw error;
    this.enabled = enabled;
    _statuses.add(
      SessionLinkHandlingStatus(
        enabled: enabled,
        registration: enabled
            ? SessionLinkRegistration.registered
            : SessionLinkRegistration.removed,
      ),
    );
  }
}

/// A registrar that only records what it was asked.
final class _RecordingRegistrar implements SessionLinkRegistrar {
  final calls = <String>[];

  @override
  Future<SessionLinkRegistrationResult> register() async {
    calls.add('register');
    return const SessionLinkRegistrationResult(
      SessionLinkRegistration.registered,
    );
  }

  @override
  Future<SessionLinkRegistrationResult> unregister() async {
    calls.add('unregister');
    return const SessionLinkRegistrationResult(SessionLinkRegistration.removed);
  }
}

void registerSessionLinkWidgetTests() {
  const server =
      'c5f39060271ebd2f22d9bf155a26bf459274e31b795145fd9d862dc206d98bb3';
  const invite = 'topiaforge://join/ABCDE12345';
  const direct = 'topiaforge://join/direct/$server/192.168.1.20/27250';

  LauncherBloc blocOf(WidgetTester tester) =>
      BlocProvider.of<LauncherBloc>(tester.element(find.byType(LauncherShell)));

  Future<void> pumpLauncher(
    WidgetTester tester, {
    required _FakeLauncherRepository repository,
    required SessionLinkInbox inbox,
    SessionLinkHandlingGateway? handling,
    LauncherUpdateRepository? updates,
    Future<void> Function()? quit,
  }) async {
    tester.view.physicalSize = const Size(1280, 2200);
    tester.view.devicePixelRatio = 1.0;
    addTearDown(tester.view.reset);
    await tester.pumpWidget(
      TopiaForgeLauncherApp(
        repository: repository,
        updateRepository: updates,
        sessionLinks: inbox.arrivals,
        sessionLinkHandling: handling,
        quitLauncher: quit ?? exitLauncher,
      ),
    );
    await tester.pumpAndSettle();
  }

  /// A link changes nothing outside the multiplayer state: no launch,
  /// restart, install, profile or section change.
  void expectNothingJoined(
    WidgetTester tester,
    _FakeLauncherRepository repository,
  ) {
    expect(repository.launchedProfileIds, isEmpty);
    expect(repository.launchedSelections, isEmpty);
    expect(repository.restartCount, 0);
    expect(repository.installPackageCount, 0);
    expect(repository.savedProfiles, isEmpty);
    expect(blocOf(tester).state.section, LauncherSection.home);
    expect(blocOf(tester).state.isBusy, isFalse);
  }

  testWidgets('a link from the command line waits for consent', (tester) async {
    final repository = _FakeLauncherRepository(snapshot: _readySnapshot());
    final inbox = SessionLinkInbox()
      ..deliver(
        const SessionLinkArrival(invite, source: SessionLinkSource.commandLine),
      );

    await pumpLauncher(tester, repository: repository, inbox: inbox);

    final intake = blocOf(tester).state.multiplayer.sessionLink;
    expect(intake, isA<SessionLinkConsentPlaceholder>());
    intake as SessionLinkConsentPlaceholder;
    expect(intake.link, SessionLink.tryParse(invite));
    expect(intake.source, SessionLinkSource.commandLine);
    expect(intake.arrival, 1);
    expect(
      find.textContaining('Received a multiplayer invite link.'),
      findsOneWidget,
    );
    expectNothingJoined(tester, repository);
  });

  testWidgets('a link handed over later arrives as the same event', (
    tester,
  ) async {
    final repository = _FakeLauncherRepository(snapshot: _readySnapshot());
    final inbox = SessionLinkInbox();
    await pumpLauncher(tester, repository: repository, inbox: inbox);

    inbox.deliver(
      const SessionLinkArrival(invite, source: SessionLinkSource.forwarded),
    );
    await tester.pumpAndSettle();

    final intake = blocOf(tester).state.multiplayer.sessionLink;
    expect(intake, isA<SessionLinkConsentPlaceholder>());
    expect(
      (intake as SessionLinkConsentPlaceholder).source,
      SessionLinkSource.forwarded,
    );
    expect(intake.link.inviteCode, 'ABCDE12345');
    expectNothingJoined(tester, repository);
  });

  testWidgets('a newer link replaces the pending one instead of queueing', (
    tester,
  ) async {
    final repository = _FakeLauncherRepository(snapshot: _readySnapshot());
    final inbox = SessionLinkInbox();
    await pumpLauncher(tester, repository: repository, inbox: inbox);

    for (final link in [invite, 'topiaforge://join/server/$server', invite]) {
      inbox.deliver(
        SessionLinkArrival(link, source: SessionLinkSource.forwarded),
      );
    }
    await tester.pumpAndSettle();

    final multiplayer = blocOf(tester).state.multiplayer;
    final intake = multiplayer.sessionLink as SessionLinkConsentPlaceholder;
    expect(intake.link.canonical, invite);
    expect(intake.arrival, 3);
    expect(multiplayer.linkArrivals, 3);
    expectNothingJoined(tester, repository);
  });

  testWidgets('invalid and oversized links are refused with their reasons', (
    tester,
  ) async {
    final repository = _FakeLauncherRepository(snapshot: _readySnapshot());
    final inbox = SessionLinkInbox();
    await pumpLauncher(tester, repository: repository, inbox: inbox);

    Future<SessionLinkRefused> refusalOf(SessionLinkArrival arrival) async {
      inbox.deliver(arrival);
      await tester.pumpAndSettle();
      final intake = blocOf(tester).state.multiplayer.sessionLink;
      expect(intake, isA<SessionLinkRefused>());
      expect(blocOf(tester).state.statusMessage, 'The link was not opened.');
      return intake as SessionLinkRefused;
    }

    final invalid = await refusalOf(
      const SessionLinkArrival(
        'topiaforge://join/ABCDE12345?x=1',
        source: SessionLinkSource.forwarded,
      ),
    );
    expect(invalid.refusal, SessionLinkRefusal.invalid);
    expect(
      blocOf(tester).state.errorMessage,
      sessionLinkRefusalMessage(SessionLinkRefusal.invalid),
    );
    expect(
      find.text(
        'The session link is damaged or incomplete. Ask the '
        'host for a new link.',
      ),
      findsOneWidget,
    );

    final oversized = await refusalOf(
      SessionLinkArrival(
        'topiaforge://join/${'A' * 600}',
        source: SessionLinkSource.commandLine,
      ),
    );
    expect(oversized.refusal, SessionLinkRefusal.tooLong);
    expect(blocOf(tester).state.errorMessage, contains('512 characters'));

    final carried = await refusalOf(
      const SessionLinkArrival.refused(
        SessionLinkRefusal.tooLong,
        source: SessionLinkSource.forwarded,
      ),
    );
    expect(carried.refusal, SessionLinkRefusal.tooLong);

    final foreign = await refusalOf(
      const SessionLinkArrival(
        'https://example.com/join/ABCDE12345',
        source: SessionLinkSource.commandLine,
      ),
    );
    expect(foreign.refusal, SessionLinkRefusal.notSessionLink);
    expectNothingJoined(tester, repository);
  });

  testWidgets(
    'no address from a direct link reaches the screen or state text',
    (tester) async {
      final repository = _FakeLauncherRepository(snapshot: _readySnapshot());
      final inbox = SessionLinkInbox();
      await pumpLauncher(tester, repository: repository, inbox: inbox);

      inbox
        ..deliver(
          const SessionLinkArrival(direct, source: SessionLinkSource.forwarded),
        )
        ..deliver(
          const SessionLinkArrival(
            'topiaforge://join/direct/$server/192.168.1.20/0',
            source: SessionLinkSource.forwarded,
          ),
        );
      await tester.pumpAndSettle();
      final state = blocOf(tester).state;
      expect(find.textContaining('192.168'), findsNothing);
      expect(state.statusMessage, isNot(contains('192.168')));
      expect(state.errorMessage ?? '', isNot(contains('192.168')));

      inbox.deliver(
        const SessionLinkArrival(direct, source: SessionLinkSource.forwarded),
      );
      await tester.pumpAndSettle();
      final intake =
          blocOf(tester).state.multiplayer.sessionLink
              as SessionLinkConsentPlaceholder;
      expect(intake.link.kind, SessionLinkKind.direct);
      expect(intake.link.toString(), isNot(contains('192.168')));
      expect(
        find.textContaining('Received a direct multiplayer link.'),
        findsOneWidget,
      );
      expect(find.textContaining('192.168'), findsNothing);
      expectNothingJoined(tester, repository);
    },
  );

  testWidgets('dismissing clears the pending link', (tester) async {
    final repository = _FakeLauncherRepository(snapshot: _readySnapshot());
    final inbox = SessionLinkInbox()
      ..deliver(
        const SessionLinkArrival(invite, source: SessionLinkSource.commandLine),
      );
    await pumpLauncher(tester, repository: repository, inbox: inbox);

    blocOf(tester).add(const SessionLinkDismissed());
    await tester.pumpAndSettle();

    expect(
      blocOf(tester).state.multiplayer.sessionLink,
      isA<SessionLinkIdle>(),
    );
    expect(blocOf(tester).state.multiplayer.linkArrivals, 1);
  });

  testWidgets('link handling applies at start and follows the setting', (
    tester,
  ) async {
    final repository = _FakeLauncherRepository();
    final handling = _FakeSessionLinkHandling();
    await pumpLauncher(
      tester,
      repository: repository,
      inbox: SessionLinkInbox(),
      handling: handling,
    );

    expect(handling.calls, ['start']);
    var status = blocOf(tester).state.multiplayer.linkHandling!;
    expect(status.enabled, isTrue);
    expect(status.registration, SessionLinkRegistration.registered);

    blocOf(tester).add(const SessionLinkHandlingChanged(false));
    await tester.pumpAndSettle();

    expect(handling.calls, ['start', 'set false']);
    status = blocOf(tester).state.multiplayer.linkHandling!;
    expect(status.enabled, isFalse);
    expect(status.registration, SessionLinkRegistration.removed);
    expect(
      find.text('TopiaForge no longer opens topiaforge:// links.'),
      findsOneWidget,
    );

    blocOf(tester).add(const LauncherRefreshRequested());
    await tester.pumpAndSettle();
    expect(handling.calls, ['start', 'set false']);
  });

  testWidgets('the real policy registers at start and follows the setting', (
    tester,
  ) async {
    final registrar = _RecordingRegistrar();
    final handling = SessionLinkHandling(
      setting: TransientSessionLinkSetting(),
      registrar: registrar,
    );
    await pumpLauncher(
      tester,
      repository: _FakeLauncherRepository(),
      inbox: SessionLinkInbox(),
      handling: handling,
    );

    expect(registrar.calls, ['register']);
    expect(
      blocOf(tester).state.multiplayer.linkHandling?.registration,
      SessionLinkRegistration.registered,
    );

    blocOf(tester).add(const SessionLinkHandlingChanged(false));
    await tester.pumpAndSettle();
    expect(registrar.calls, ['register', 'unregister']);
    expect(blocOf(tester).state.multiplayer.linkHandling?.enabled, isFalse);

    blocOf(tester).add(const SessionLinkHandlingChanged(false));
    await tester.pumpAndSettle();
    expect(registrar.calls, ['register', 'unregister']);

    blocOf(tester).add(const SessionLinkHandlingChanged(true));
    await tester.pumpAndSettle();
    expect(registrar.calls, ['register', 'unregister', 'register']);
    expect(find.text('TopiaForge opens topiaforge:// links.'), findsOneWidget);
  });

  testWidgets('a setting that cannot be saved keeps the last status', (
    tester,
  ) async {
    final handling = _FakeSessionLinkHandling()
      ..saveError = StateError('disk full');
    await pumpLauncher(
      tester,
      repository: _FakeLauncherRepository(),
      inbox: SessionLinkInbox(),
      handling: handling,
    );

    blocOf(tester).add(const SessionLinkHandlingChanged(false));
    await tester.pumpAndSettle();

    final state = blocOf(tester).state;
    expect(state.multiplayer.linkHandling!.enabled, isTrue);
    expect(state.statusMessage, 'The link setting was not saved.');
    expect(state.errorMessage, contains('disk full'));
  });

  testWidgets('without link handling the setting is reported unavailable', (
    tester,
  ) async {
    await pumpLauncher(
      tester,
      repository: _FakeLauncherRepository(),
      inbox: SessionLinkInbox(),
    );

    expect(blocOf(tester).state.multiplayer.linkHandling, isNull);
    blocOf(tester).add(const SessionLinkHandlingChanged(false));
    await tester.pumpAndSettle();
    expect(
      blocOf(tester).state.statusMessage,
      'Link handling is not available here.',
    );
  });

  testWidgets('applying an update quits the launcher once', (tester) async {
    var quits = 0;
    final updates = _FakeLauncherUpdateRepository();
    await pumpLauncher(
      tester,
      repository: _FakeLauncherRepository(),
      inbox: SessionLinkInbox(),
      updates: updates,
      quit: () async => quits++,
    );

    updates.emit(
      const LauncherUpdateStatus(
        phase: LauncherUpdatePhase.applying,
        message: 'Restarting into the verified launcher update.',
      ),
    );
    await tester.pumpAndSettle();

    expect(quits, 1);
  });

  test('the launcher quits by asking the embedder for a required exit', () {
    expect(
      TopiaForgeLauncherApp(repository: _FakeLauncherRepository()).quitLauncher,
      same(exitLauncher),
    );
    // The test binding refuses a required exit, which is exactly what
    // exitLauncher asks for; SystemNavigator.pop asks for nothing on Windows.
    expect(
      exitLauncher(),
      throwsA(
        isA<FlutterError>().having(
          (error) => error.message,
          'message',
          contains('Unexpected application exit request'),
        ),
      ),
    );
  });
}
