import 'dart:async';
import 'dart:convert';
import 'dart:io' show File, FileSystemException, Platform;
import 'dart:math' as math;
import 'dart:ui' as ui;

import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:flutter_bloc/flutter_bloc.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:launcher_domain/launcher_domain.dart';
import 'package:launcher_ui/launcher_ui.dart';
import 'package:topiaforge_launcher_flutter/src/launcher_app.dart';
import 'package:topiaforge_launcher_flutter/src/launcher_bloc.dart';
import 'package:topiaforge_launcher_flutter/src/launcher_event.dart';
import 'package:topiaforge_launcher_flutter/src/launcher_multiplayer_state.dart';
import 'package:topiaforge_launcher_flutter/src/launcher_state.dart';
import 'package:topiaforge_launcher_flutter/src/session_link_inbox.dart';

part 'join_flow_fakes.dart';
part 'join_flow_bloc_test_cases.dart';
part 'join_flow_failure_test_cases.dart';
part 'join_flow_widget_test_cases.dart';
part 'join_flow_layout_test_cases.dart';
part 'join_flow_privacy_test_cases.dart';
part 'join_flow_host_served_test_cases.dart';
part 'join_consent_generation_test_cases.dart';
part 'join_address_surfaces_test_cases.dart';
part 'join_flow_golden_test_cases.dart';

/// A server link, an invite, a direct link to a host on the local network,
/// and a typed public address that only a direct connection reaches.
const _serverId =
    'c5f39060271ebd2f22d9bf155a26bf459274e31b795145fd9d862dc206d98bb3';
final _serverLink = SessionLink.server(_serverId);
final _publicAddress = JoinAddressTarget(host: _planted, port: 27250);

/// A started bloc over [repository] and [gateway].
Future<LauncherBloc> _startedBloc(
  _JoinRepository repository,
  _JoinGateway gateway, {
  Stream<SessionLinkArrival>? links,
}) async {
  final bloc = LauncherBloc(
    repository,
    multiplayer: gateway,
    sessionLinks: links,
  );
  addTearDown(bloc.close);
  bloc.add(const LauncherStarted());
  await _until(bloc, (state) => state.snapshotLoaded);
  return bloc;
}

/// The bloc's state once [test] holds.
Future<LauncherState> _until(
  LauncherBloc bloc,
  bool Function(LauncherState state) test,
) async {
  if (test(bloc.state)) return bloc.state;
  return bloc.stream.firstWhere(test).timeout(const Duration(seconds: 10));
}

JoinFlow? _flowOf(LauncherBloc bloc) => bloc.state.multiplayer.join.flow;

MultiplayerJoinConsented _consent(
  LauncherBloc bloc, {
  bool closeOwnedGame = false,
}) => MultiplayerJoinConsented(
  flow: _flowOf(bloc)!.id,
  consent: _flowOf(bloc)!.join!,
  closeOwnedGame: closeOwnedGame,
);

/// Waits until the running join reaches [stage].
Future<JoinFlow> _atStage(LauncherBloc bloc, JoinStage stage) async {
  await _until(
    bloc,
    (state) => state.multiplayer.join.flow?.join?.stage == stage,
  );
  return _flowOf(bloc)!;
}

/// Waits until the game has taken the running join, so its records count.
Future<JoinFlow> _launched(LauncherBloc bloc) async {
  await _until(
    bloc,
    (state) =>
        state.multiplayer.join.flow?.join?.stage == JoinStage.launching &&
        state.multiplayer.join.flow?.launch != null,
  );
  return _flowOf(bloc)!;
}

/// Waits until the flow ends with a failure, and returns it.
Future<JoinFailure> _failed(LauncherBloc bloc) async {
  await _until(
    bloc,
    (state) => state.multiplayer.join.flow?.join?.outcome?.failure != null,
  );
  return _flowOf(bloc)!.join!.outcome!.failure!;
}

/// A bloc whose join of the server link waits at the consent sheet.
Future<(LauncherBloc, _JoinGateway, _JoinRepository)> _atConsent({
  _JoinGateway? gateway,
  _JoinRepository? repository,
}) async {
  final multiplayer = gateway ?? _JoinGateway();
  final launcher = repository ?? _JoinRepository(install: _install);
  final bloc = await _startedBloc(launcher, multiplayer);
  bloc.add(MultiplayerJoinRequested(JoinLinkTarget(_serverLink)));
  await _atStage(bloc, JoinStage.awaitingConsent);
  return (bloc, multiplayer, launcher);
}

void main() {
  final binding = TestWidgetsFlutterBinding.ensureInitialized();
  // The suite runs with reduced motion, so every settle ends; the step rail
  // and the waiting lines then hold still.
  setUp(() {
    binding.platformDispatcher.accessibilityFeaturesTestValue =
        const FakeAccessibilityFeatures(disableAnimations: true);
  });
  tearDown(binding.platformDispatcher.clearAccessibilityFeaturesTestValue);

  registerJoinBlocTests();
  registerJoinFailureTests();
  registerJoinWidgetTests();
  registerJoinLayoutTests();
  registerJoinPrivacyTests();
  registerJoinHostServedTests();
  registerConsentGenerationTests();
  registerAddressSurfaceTests();
  registerJoinGoldenRenders();
}
