import 'dart:async';
import 'dart:io';

import 'package:flutter/material.dart';
import 'package:flutter_bloc/flutter_bloc.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:launcher_domain/launcher_domain.dart';
import 'package:launcher_ui/launcher_ui.dart';
import 'package:topiaforge_launcher_flutter/src/launcher_bloc.dart';
import 'package:topiaforge_launcher_flutter/src/launcher_event.dart';
import 'package:topiaforge_launcher_flutter/src/launcher_multiplayer_state.dart';
import 'package:topiaforge_launcher_flutter/src/launcher_state.dart';
import 'package:topiaforge_launcher_flutter/src/screens.dart';

part 'hosting_flow_fakes.dart';

Future<void> _until(
  LauncherBloc bloc,
  bool Function(LauncherState) predicate,
) async {
  if (predicate(bloc.state)) return;
  await bloc.stream.firstWhere(predicate).timeout(const Duration(seconds: 5));
}

Future<LauncherBloc> _open(_Rig rig) async {
  final bloc = LauncherBloc(rig, multiplayer: rig);
  addTearDown(bloc.close);
  bloc.add(const LauncherStarted());
  await _until(bloc, (s) => s.snapshotLoaded && !s.isBusy);
  bloc.add(const HostingOpened());
  await _until(
    bloc,
    (s) => s.multiplayer.hosting.phase == HostingPhase.editing,
  );
  return bloc;
}

/// The block-quoted paragraphs of one section of the approved privacy text,
/// with each paragraph's wrapped lines joined by single spaces.
List<String> _approvedQuotes(String heading) {
  final lines = File(
    '../../docs/internal/MultiplayerPrivacyText.md',
  ).readAsLinesSync();
  final start = lines.indexOf('## $heading');
  expect(start, isNot(-1), reason: 'the approved text has "$heading"');
  final quotes = <String>[];
  var current = <String>[];
  for (final line in lines.skip(start + 1)) {
    if (line.startsWith('## ')) break;
    final quoted = line.startsWith('>') ? line.substring(1).trim() : null;
    if (quoted != null && quoted.isNotEmpty) {
      current.add(quoted);
    } else if (current.isNotEmpty) {
      quotes.add(current.join(' '));
      current = [];
    }
  }
  if (current.isNotEmpty) quotes.add(current.join(' '));
  return quotes;
}

Future<void> _review(LauncherBloc bloc, _Rig rig) async {
  bloc.add(HostingReviewRequested(rig.choices));
  await _until(bloc, (s) => s.multiplayer.hosting.phase == HostingPhase.review);
}

void main() {
  test(
    'slow host preparation leaves the event queue responsive and drops a late answer',
    () async {
      final rig = _Rig()..prepareGate = Completer();
      final bloc = await _open(rig);
      bloc.add(HostingReviewRequested(rig.choices));
      await _until(
        bloc,
        (s) => s.multiplayer.hosting.phase == HostingPhase.preparing,
      );
      bloc.add(const HostingClosed());
      await _until(
        bloc,
        (s) => s.multiplayer.hosting.phase == HostingPhase.closed,
      );
      expect(rig.token!.isCancelled, isTrue);
      rig.prepareGate!.complete(rig.plan());
      await Future<void>.delayed(Duration.zero);
      expect(bloc.state.multiplayer.hosting.phase, HostingPhase.closed);
      expect(rig.launches, 0);
    },
  );
  test(
    'changing the profile cancels a start already waiting on disk',
    () async {
      final rig = _Rig()
        ..startGate = Completer()
        ..saveGate = Completer();
      final bloc = await _open(rig);
      await _review(bloc, rig);
      bloc.add(const HostingConfirmed(acknowledgeDisclosure: true));
      await _until(
        bloc,
        (s) => s.multiplayer.hosting.phase == HostingPhase.starting,
      );
      bloc.add(const ProfileSelected('other'));
      await pumpEventQueue();
      expect(rig.token!.isCancelled, isTrue);
      expect(bloc.state.selectedProfile?.id, 'default');
      rig.startGate!.complete();
      rig.saveGate!.complete();
      await _until(bloc, (s) => s.selectedProfile?.id == 'other');
      await _until(
        bloc,
        (s) => s.multiplayer.hosting.phase == HostingPhase.closed,
      );
      expect(rig.launches, 0);
    },
  );
  test('duplicate confirm starts only once', () async {
    final rig = _Rig()..startGate = Completer();
    final bloc = await _open(rig);
    await _review(bloc, rig);
    bloc
      ..add(const HostingConfirmed(acknowledgeDisclosure: true))
      ..add(const HostingConfirmed(acknowledgeDisclosure: true));
    await _until(
      bloc,
      (s) => s.multiplayer.hosting.phase == HostingPhase.starting,
    );
    rig.startGate!.complete();
    await _until(
      bloc,
      (s) => s.multiplayer.hosting.phase == HostingPhase.failed,
    );
    expect(rig.launches, 1);
  });
  for (final refusal in ServerContentCleanupRefusal.values) {
    test(
      'cleanup uses the reviewed plan and explains $refusal without raw details',
      () async {
        final rig = _Rig()..refusal = refusal;
        final bloc = await _open(rig);
        bloc.add(const ServerCleanupRequested());
        await _until(bloc, (s) => s.multiplayer.cleanup.catalog != null);
        bloc.add(const ServerCleanupRequested(profiles: {'server-one'}));
        await _until(bloc, (s) => s.multiplayer.cleanup.reviewing);
        final plan = bloc.state.multiplayer.cleanup.plan!;
        bloc.add(const ServerCleanupConfirmed('wrong'));
        await Future<void>.delayed(Duration.zero);
        expect(rig.applied, isNull);
        bloc.add(ServerCleanupConfirmed(plan.fingerprint));
        await _until(bloc, (s) => s.multiplayer.cleanup.message.isNotEmpty);
        expect(identical(rig.applied, plan), isTrue);
        expect(
          bloc.state.multiplayer.cleanup.message,
          isNot(contains('203.0.113.77')),
        );
        expect(bloc.state.multiplayer.cleanup.plan, isNull);
      },
    );
  }
  test('closed cleanup drops a late plan', () async {
    final rig = _Rig()..cleanupGate = Completer();
    final bloc = await _open(rig);
    bloc.add(const ServerCleanupRequested());
    await _until(bloc, (s) => s.multiplayer.cleanup.busy);
    bloc.add(const ServerCleanupClosed());
    await _until(bloc, (s) => !s.multiplayer.cleanup.busy);
    rig.cleanupGate!.complete(rig.catalog);
    await Future<void>.delayed(Duration.zero);
    expect(bloc.state.multiplayer.cleanup.catalog, isNull);
  });
  test('successful cleanup refreshes the launcher profiles', () async {
    final rig = _Rig()..refusal = null;
    final bloc = await _open(rig);
    bloc.add(const ServerCleanupRequested(profiles: {'server-one'}));
    await _until(bloc, (s) => s.multiplayer.cleanup.reviewing);
    final before = rig.loads;
    bloc.add(
      ServerCleanupConfirmed(bloc.state.multiplayer.cleanup.plan!.fingerprint),
    );
    await _until(
      bloc,
      (s) => s.multiplayer.cleanup.message.startsWith('Removed'),
    );
    await pumpEventQueue();
    expect(rig.loads, greaterThan(before));
  });
  test('the public activity notice is its approved section word for word', () {
    expect(_approvedQuotes('When a player hosts a public Internet session'), [
      HostActivityText.publicSessions,
    ]);
  });
  testWidgets('the activity notice shows for a public session only', (
    tester,
  ) async {
    final rig = _Rig();
    late LauncherBloc bloc;
    await tester.runAsync(() async {
      bloc = await _open(rig);
    });
    await tester.pumpWidget(
      BlocProvider.value(
        value: bloc,
        child: const MaterialApp(home: Scaffold(body: HostingDialog())),
      ),
    );
    await tester.pumpAndSettle();
    for (final (label, shown) in [
      ('Public', true),
      ('Invite only', false),
      ('Local network (LAN)', false),
      ('Public', true),
    ]) {
      await tester.tap(find.byType(DropdownButtonFormField<ServerVisibility>));
      await tester.pumpAndSettle();
      await tester.tap(find.text(label).last);
      await tester.pumpAndSettle();
      expect(
        find.text(HostActivityText.publicSessions),
        shown ? findsOneWidget : findsNothing,
        reason: label,
      );
      expect(
        find.byKey(const Key('hosting-activity-disclosure')),
        shown ? findsOneWidget : findsNothing,
        reason: label,
      );
    }
  });
  for (final size in [
    const Size(2560, 1440),
    const Size(1920, 1080),
    const Size(900, 600),
  ]) {
    testWidgets('host and cleanup fit $size with larger text', (tester) async {
      tester.view
        ..physicalSize = size
        ..devicePixelRatio = 1;
      addTearDown(tester.view.resetPhysicalSize);
      addTearDown(tester.view.resetDevicePixelRatio);
      final rig = _Rig();
      late LauncherBloc bloc;
      await tester.runAsync(() async {
        bloc = await _open(rig);
      });
      Widget page(Widget child) => BlocProvider.value(
        value: bloc,
        child: MaterialApp(
          home: MediaQuery(
            data: MediaQueryData(
              size: size,
              textScaler: const TextScaler.linear(1.5),
            ),
            child: Scaffold(body: child),
          ),
        ),
      );
      await tester.pumpWidget(page(const HostingDialog()));
      await tester.pumpAndSettle();
      expect(find.byKey(const Key('hosting-name')), findsOneWidget);
      final visibility = find.byType(DropdownButtonFormField<ServerVisibility>);
      await tester.tap(visibility);
      await tester.pumpAndSettle();
      await tester.tap(find.text('Public').last);
      await tester.pumpAndSettle();
      expect(find.text(HostActivityText.publicSessions), findsOneWidget);
      expect(find.byType(TextField), findsNWidgets(2)); // no password field
      expect(tester.takeException(), isNull);
      await tester.runAsync(() async {
        await _review(bloc, rig);
      });
      await tester.pumpAndSettle();
      expect(
        find.text(MultiplayerPrivacyText.disclosureHidden),
        findsOneWidget,
      );
      expect(tester.takeException(), isNull);
      await tester.runAsync(() async {
        bloc.add(const ServerCleanupRequested());
        await _until(bloc, (s) => s.multiplayer.cleanup.catalog != null);
      });
      await tester.pumpWidget(page(const ServerCleanupDialog()));
      await tester.pumpAndSettle();
      expect(find.text('Evening server'), findsOneWidget);
      expect(find.text('Review removal'), findsOneWidget);
      expect(tester.takeException(), isNull);
    });
  }
  testWidgets('first direct host reads disclosure once before direct consent', (
    tester,
  ) async {
    late _Rig rig;
    late LauncherBloc bloc;
    await tester.runAsync(() async {
      rig = _Rig()..prepareGate = Completer<HostingPreparation>();
      bloc = await _open(rig);
      bloc.add(HostingReviewRequested(rig.choices));
      await _until(
        bloc,
        (s) => s.multiplayer.hosting.phase == HostingPhase.preparing,
      );
      rig.prepareGate!.complete(
        const HostingRefused('Relay unavailable.', canAllowDirect: true),
      );
      await _until(
        bloc,
        (s) => s.multiplayer.hosting.phase == HostingPhase.failed,
      );
    });
    await tester.pumpWidget(
      BlocProvider.value(
        value: bloc,
        child: const MaterialApp(home: Scaffold(body: HostingDialog())),
      ),
    );
    await tester.pumpAndSettle();
    await tester.tap(find.text('Allow a direct connection…'));
    await tester.pumpAndSettle();
    expect(find.byKey(const Key('privacy-disclosure-dialog')), findsOneWidget);
    expect(find.text(MultiplayerPrivacyText.directTitle), findsNothing);
    expect(rig.launches, 0);
    await tester.tap(find.text(MultiplayerPrivacyText.disclosureButton));
    await tester.pumpAndSettle();
    expect(find.byKey(const Key('privacy-disclosure-dialog')), findsNothing);
    expect(find.text(MultiplayerPrivacyText.directTitle), findsOneWidget);
    rig.prepareGate = null;
    await tester.tap(find.text(MultiplayerPrivacyText.directAllowButton));
    await tester.pumpAndSettle();
    await tester.runAsync(() async {
      await _until(
        bloc,
        (s) => s.multiplayer.hosting.phase == HostingPhase.review,
      );
    });
    await tester.pumpAndSettle();
    expect(find.text(MultiplayerPrivacyText.directBody), findsOneWidget);
    await tester.tap(find.text('Start hosting'));
    await tester.pumpAndSettle();
    expect(find.byKey(const Key('privacy-disclosure-dialog')), findsNothing);
    await tester.runAsync(() async {
      await _until(
        bloc,
        (s) => s.multiplayer.hosting.phase == HostingPhase.failed,
      );
    });
    expect(rig.launches, 1);
    expect(rig.disclosureSaved, isTrue);
    expect(tester.takeException(), isNull);
  });
}
