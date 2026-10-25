part of 'join_flow_test.dart';

/// A direct link to a host on the local network: a host the launcher reaches
/// itself, so the plan may take the host's own mods from it.
final _lanLink = JoinLinkTarget(
  SessionLink.direct(serverId: _serverId, host: '192.168.1.20', port: 27250),
);

/// A server whose host serves its packages, and a plan made in developer
/// mode that takes [hostedId] from the host and finds Sandbox installed,
/// declaring [capabilities].
Future<(VerifiedServerDescriptor, JoinPlan)> _hostServedPlan({
  String hostedId = 'tf.zombies',
  List<String> capabilities = const [],
}) async {
  final server = await _server(
    hostServesPackages: true,
    packages: [
      DescriptorPackage(
        id: hostedId,
        version: '2.1.0',
        sha256: _sha('zombies'),
        role: DescriptorPackageRole.implicit,
      ),
      DescriptorPackage(
        id: 'tf.sandbox',
        version: '1.4.0',
        sha256: _sha('sandbox'),
        role: DescriptorPackageRole.implicit,
      ),
    ],
  );
  final plan = const JoinPlanner().plan(
    server: server,
    resolutions: {
      _sha('zombies'): JoinPackageResolution.hostOffered(),
      _sha('sandbox'): JoinPackageResolution.installed(
        _manifest(
          'tf.sandbox',
          '1.4.0',
          name: 'Sandbox',
          capabilities: capabilities,
        ),
      ),
    },
    environment: JoinEnvironment(
      gameBuild: _environment.gameBuild,
      runtimeVersion: _environment.runtimeVersion,
      sdkVersion: _environment.sdkVersion,
      protocolVersion: _environment.protocolVersion,
      developerMode: true,
    ),
    mayContactHost: true,
  );
  expect(plan.hasUnverifiedPackages, isTrue);
  return (server, plan);
}

/// A bloc whose join of the host on the local network waits at its consent,
/// with a plan that takes a mod from the host.
Future<(LauncherBloc, _JoinGateway)> _atHostServedConsent() async {
  final (server, plan) = await _hostServedPlan();
  final gateway = _JoinGateway(server: server, plan: plan);
  final bloc = await _startedBloc(_JoinRepository(install: _install), gateway);
  bloc.add(MultiplayerJoinRequested(_lanLink));
  await _atStage(bloc, JoinStage.awaitingConsent);
  return (bloc, gateway);
}

/// The launcher on screen at the consent sheet of a join whose plan takes
/// [hostedId] from the host.
Future<_JoinGateway> _toHostServedSheet(
  WidgetTester tester, {
  Size size = const Size(1280, 900),
  String hostedId = 'tf.zombies',
  List<String> capabilities = const [],
}) async {
  final (server, plan) = (await tester.runAsync(
    () => _hostServedPlan(hostedId: hostedId, capabilities: capabilities),
  ))!;
  final (gateway, _) = await _openApp(
    tester,
    size: size,
    gateway: _JoinGateway(server: server, plan: plan),
  );
  _blocIn(tester).add(MultiplayerJoinRequested(_lanLink));
  await tester.pumpAndSettle();
  expect(find.byKey(const Key('join-host-served')), findsOneWidget);
  return gateway;
}

/// Whether the sheet's Join button can be pressed.
bool _joinOffered(WidgetTester tester) =>
    tester
        .widget<ButtonStyleButton>(find.byKey(const Key('join-consent-join')))
        .onPressed !=
    null;

/// Taps the developer-mode box, scrolled into view first.
Future<void> _tapHostServedBox(WidgetTester tester) async {
  final box = find.byKey(const Key('join-host-served-box'));
  await tester.ensureVisible(box);
  await tester.pumpAndSettle();
  await tester.tap(box);
  await tester.pumpAndSettle();
}

/// The privacy document's text, one line per paragraph, as the shared UI's
/// test reads it.
String _privacyDocument() {
  final paragraphs = <String>[];
  var current = <String>[];
  final file = File('../../docs/internal/MultiplayerPrivacyText.md');
  for (final raw in file.readAsLinesSync()) {
    final line = raw.startsWith('>') ? raw.substring(1).trim() : raw.trim();
    if (line.isEmpty) {
      if (current.isNotEmpty) paragraphs.add(current.join(' '));
      current = [];
    } else {
      current.add(line);
    }
  }
  if (current.isNotEmpty) paragraphs.add(current.join(' '));
  return paragraphs.join('\n');
}

void registerJoinHostServedTests() {
  group('Mods only the host has', () {
    test('a consent waits for the developer-mode box, which counts only for '
        'its own flow, and the join keeps the agreement', () async {
      final (bloc, gateway) = await _atHostServedConsent();
      final id = _flowOf(bloc)!.id;
      bool? acknowledged;
      gateway.answers[JoinStage.downloading] = (state) {
        acknowledged = state.hostServedAcknowledged;
        return const JoinDownloadsCompleted();
      };

      // Unticked, the reducer does not take the consent.
      bloc.add(MultiplayerJoinConsented(flow: id));
      await pumpEventQueue();
      expect(_flowOf(bloc)!.join!.stage, JoinStage.awaitingConsent);

      // A tick for another flow counts for nothing; a cleared box holds the
      // join again.
      bloc.add(MultiplayerJoinHostServedTicked(flow: id + 1, ticked: true));
      await pumpEventQueue();
      expect(_flowOf(bloc)!.hostServedTicked, isFalse);
      bloc
        ..add(MultiplayerJoinHostServedTicked(flow: id, ticked: true))
        ..add(MultiplayerJoinHostServedTicked(flow: id, ticked: false))
        ..add(MultiplayerJoinConsented(flow: id));
      await pumpEventQueue();
      expect(_flowOf(bloc)!.join!.stage, JoinStage.awaitingConsent);
      expect(gateway.calls, isNot(contains('answer downloading')));

      bloc
        ..add(MultiplayerJoinHostServedTicked(flow: id, ticked: true))
        ..add(MultiplayerJoinConsented(flow: id));
      final flow = await _atStage(bloc, JoinStage.launching);
      expect(acknowledged, isTrue);
      expect(flow.join!.hostServedAcknowledged, isTrue);
      // The box is forgotten once the join leaves its consent.
      expect(flow.hostServedTicked, isFalse);
    });

    test('a join tried again asks for the box afresh', () async {
      final (bloc, _) = await _atHostServedConsent();
      final id = _flowOf(bloc)!.id;
      bloc.add(MultiplayerJoinHostServedTicked(flow: id, ticked: true));
      await pumpEventQueue();
      expect(_flowOf(bloc)!.hostServedTicked, isTrue);
      bloc.add(MultiplayerJoinCancelled(id));
      expect((await _failed(bloc)).code, JoinFailureCode.cancelled);

      bloc.add(MultiplayerJoinRetried(id));
      final again = await _atStage(bloc, JoinStage.awaitingConsent);
      expect(again.hostServedTicked, isFalse);
    });

    test('a plan with no mod from the host has no box to tick', () async {
      final (bloc, _, _) = await _atConsent();
      final id = _flowOf(bloc)!.id;
      bloc.add(MultiplayerJoinHostServedTicked(flow: id, ticked: true));
      await pumpEventQueue();
      expect(_flowOf(bloc)!.hostServedTicked, isFalse);
      bloc.add(MultiplayerJoinConsented(flow: id));
      final flow = await _atStage(bloc, JoinStage.launching);
      expect(flow.join!.hostServedAcknowledged, isFalse);
    });

    testWidgets('the sheet shows the red section and the mod as the host '
        'lists it, says what it may do is not known, and keeps Join off '
        'until the box is ticked', (tester) async {
      await _toHostServedSheet(tester);
      for (final text in [
        JoinHostServedText.title,
        JoinHostServedText.warning,
        JoinHostServedText.checkbox,
        JoinHostServedText.untickedHint,
      ]) {
        expect(find.text(text), findsOneWidget);
      }
      // No source vouches for it: its id and version, SHA-256 and where it
      // comes from.
      expect(find.text('tf.zombies@2.1.0'), findsOneWidget);
      expect(find.text('${_sha('zombies').substring(0, 12)}…'), findsOneWidget);
      expect(find.text('Served by the host'), findsOneWidget);
      expect(
        find.byKey(const Key('join-host-served-capabilities')),
        findsOneWidget,
      );
      expect(find.text('No capabilities declared.'), findsNothing);
      expect(
        find.text(
          'Nothing to download from your mod sources. 1 mod comes from the '
          'host itself.',
        ),
        findsOneWidget,
      );
      expect(_joinOffered(tester), isFalse);

      await _tapHostServedBox(tester);
      expect(_joinOffered(tester), isTrue);
      expect(find.text(JoinHostServedText.untickedHint), findsNothing);
      await _tapHostServedBox(tester);
      expect(_joinOffered(tester), isFalse);

      await _tapHostServedBox(tester);
      await tester.tap(find.byKey(const Key('join-consent-join')));
      await tester.pumpAndSettle();
      expect(find.byKey(const Key('join-consent-sheet')), findsNothing);
      expect(_flowOf(_blocIn(tester))!.join!.hostServedAcknowledged, isTrue);
    });

    testWidgets('what the mods from your sources declare still shows beside '
        'what is not known', (tester) async {
      await _toHostServedSheet(tester, capabilities: ['network']);
      expect(
        find.descendant(
          of: find.byType(CapabilityChips),
          matching: find.text('network'),
        ),
        findsOneWidget,
      );
      expect(
        find.byKey(const Key('join-host-served-capabilities')),
        findsOneWidget,
      );
    });

    testWidgets('the keyboard reaches the box from Cancel, Space ticks it, '
        'and Join comes next', (tester) async {
      await _toHostServedSheet(tester);
      expect(_focused(const Key('join-consent-cancel')), isTrue);
      // Join is off, so Tab goes on from Cancel to the box.
      await tester.sendKeyEvent(LogicalKeyboardKey.tab);
      await tester.pump();
      expect(_focused(const Key('join-host-served-box')), isTrue);
      await tester.sendKeyEvent(LogicalKeyboardKey.space);
      await tester.pumpAndSettle();
      expect(_flowOf(_blocIn(tester))!.hostServedTicked, isTrue);
      expect(_joinOffered(tester), isTrue);
      await tester.sendKeyEvent(LogicalKeyboardKey.tab);
      await tester.pump();
      expect(_focused(const Key('join-consent-cancel')), isTrue);
      await tester.sendKeyEvent(LogicalKeyboardKey.tab);
      await tester.pump();
      expect(_focused(const Key('join-consent-join')), isTrue);
      await tester.sendKeyEvent(LogicalKeyboardKey.space);
      await tester.pumpAndSettle();
      expect(find.byKey(const Key('join-consent-sheet')), findsNothing);
    });

    testWidgets('a download from the host the launcher refuses names what is '
        'missing', (tester) async {
      final gateway = await _toHostServedSheet(tester);
      gateway.answers[JoinStage.downloading] = (_) =>
          JoinFailed(JoinFailureCode.hostDownloadUnsupported);
      await _tapHostServedBox(tester);
      await tester.tap(find.byKey(const Key('join-consent-join')));
      await tester.pumpAndSettle();
      expect(find.byKey(const Key('join-failure-card')), findsOneWidget);
      final body = tester
          .widget<Text>(find.byKey(const Key('join-failure-body')))
          .data!;
      for (final missing in [
        'developer mode',
        "the join's developer-mode section",
        'rather than through the TopiaForge service',
        'publish it to a mod source you use',
      ]) {
        expect(body, contains(missing));
      }
      expect(body, isNot(contains('yet')));
    });

    testWidgets('a mod named like an address shows no address on the sheet, '
        'on the card of a join through the service, or in its copy', (
      tester,
    ) async {
      await _recording((said) async {
        _captureClipboard(tester);
        await _toHostServedSheet(tester, hostedId: _planted);
        _expectNoAddress(_screenTexts(tester), 'the consent sheet');
        expect(find.text(withheldAddress), findsOneWidget);
        expect(find.text('$withheldAddress@2.1.0'), findsOneWidget);
        final bloc = _blocIn(tester);
        bloc.add(MultiplayerJoinCancelled(_flowOf(bloc)!.id));
        await tester.pumpAndSettle();
        bloc.add(MultiplayerJoinClosed(_flowOf(bloc)!.id));
        await tester.pumpAndSettle();

        // A host the service found is never asked, so the plan's blockers
        // name the mod instead.
        bloc.add(MultiplayerJoinRequested(JoinLinkTarget(_serverLink)));
        await tester.pumpAndSettle();
        final failure = _flowOf(bloc)!.join!.outcome!.failure!;
        expect(failure.code, JoinFailureCode.contentUnavailable);
        expect(failure.blockers.single.modId, _planted);
        expect(find.byKey(const Key('join-failure-card')), findsOneWidget);
        _expectNoAddress(_screenTexts(tester), 'the failure card');
        await tester.tap(find.byKey(const Key('join-copy-details')));
        await tester.pumpAndSettle();
        _expectNoAddress([_copied!], 'the copied details');
        _expectNoAddress(said.texts, 'what the launcher said');
      });
    });

    test('the section says what the draft awaiting approval says, word for '
        'word', () {
      final lines = _privacyDocument().split('\n');
      final heading = lines.indexWhere(
        (line) =>
            line.startsWith('## ') && line.endsWith('(awaiting approval)'),
      );
      expect(heading, isNonNegative, reason: 'the draft is marked as one');
      final next = lines.indexWhere(
        (line) => line.startsWith('## '),
        heading + 1,
      );
      final draft = lines.sublist(heading + 1, next < 0 ? lines.length : next);
      expect(draft, contains('**Title:** ${JoinHostServedText.title}'));
      expect(draft, contains(JoinHostServedText.warning));
      expect(draft, contains('**Checkbox:** ${JoinHostServedText.checkbox}'));
      expect(
        draft,
        contains('**Until it is ticked:** ${JoinHostServedText.untickedHint}'),
      );
    });
  });
}
