import 'dart:async';

import 'package:launcher_data/launcher_data.dart';
import 'package:launcher_data/src/multiplayer/hosting_service.dart';
import 'package:launcher_domain/launcher_domain.dart';
import 'package:test/test.dart';

final class Rig
    implements LauncherRepository, MultiplayerGateway, MultiplayerHostingHost {
  final install = const GameInstall(
    path: 'fixture',
    executablePath: 'fixture/game.exe',
    bepInExStatus: ComponentState.ready,
    loaderStatus: ComponentState.ready,
  );
  var profile = LauncherProfile.defaultProfile();
  var preferences = MultiplayerPreferences(
    settings: MultiplayerSettings(),
    hosting: MultiplayerHostingDefaults.forPlayer('Player'),
  );
  GameRunState running = GameRunState.notRunning;
  bool syncFails = false,
      blocked = false,
      throwPreview = false,
      relay = true,
      available = true;
  String digest = 'digest';
  int launches = 0, reads = 0;
  LaunchSessionRequest? request;
  Future<void> Function()? beforePreview, beforeSave;
  CancellationToken? launchToken;
  String? launchedDigest;
  HostingService get service => HostingService(
    launcher: this,
    multiplayer: this,
    serviceOrigin: 'https://play.example',
    serviceStatus: (_) async {
      reads++;
      return available
          ? MultiplayerServiceCheck.available(
              MultiplayerServiceFeatures(
                hosting: true,
                invites: true,
                signaling: true,
                relay: relay,
                killSwitch: false,
                heartbeat: const Duration(seconds: 10),
                listingTtl: const Duration(seconds: 30),
                relayCredentialTtl: const Duration(minutes: 5),
              ),
            )
          : const MultiplayerServiceCheck.unavailable(
              MultiplayerServiceProblem.unreachable,
              'private endpoint',
            );
    },
  );
  HostingChoices choices({
    ServerVisibility visibility = ServerVisibility.lan,
    bool direct = false,
    bool voice = false,
    bool serve = false,
    int port = 0,
  }) => HostingChoices(
    defaults: MultiplayerHostingDefaults(
      name: 'Evening game',
      maxPlayers: 12,
      visibility: visibility,
      port: port,
      voice: voice,
    ),
    passwordRequired: true,
    allowDirect: direct,
    servePackages: serve,
  );
  Future<HostingPreparation> prepare({
    HostingChoices? options,
    CancellationToken? token,
  }) => service.prepare(
    install,
    profile,
    options ?? choices(),
    developerMode: false,
    cancellation: token ?? CancellationToken(),
  );
  Future<LaunchResult> start(
    HostingPlan plan, {
    bool disclosure = true,
    CancellationToken? token,
  }) => service.start(
    install,
    plan,
    intentId: 'intent-one',
    acknowledgeDisclosure: disclosure,
    developerMode: false,
    cancellation: token ?? CancellationToken(),
  );
  @override
  Future<GameRunState> gameRunState(GameInstall _) async => running;
  @override
  Future<MultiplayerPreferencesRound> syncWithGame(
    GameInstall _, {
    CancellationToken? cancellation,
  }) async => syncFails
      ? MultiplayerPreferencesRoundSkipped(preferences)
      : MultiplayerPreferencesSynced(preferences, changedInGame: false);
  @override
  Future<MultiplayerPreferences> changePreferences(
    MultiplayerPreferencesChange change,
  ) async {
    await beforeSave?.call();
    return preferences = change.applyTo(preferences);
  }

  @override
  Future<LaunchPreview> previewLaunch(
    GameInstall install,
    LauncherProfile profile, {
    LaunchSelection? selectionOverride,
  }) async {
    await beforePreview?.call();
    if (throwPreview) throw StateError('203.0.113.77 private path');
    return LaunchPreview(
      profileId: profile.id,
      profileRevision: profile.revision,
      installIdentity: 'install',
      packageDigest: digest,
      requestedSelection: profile.launchSelection,
      effectiveSelection: const LaunchSelection.mainMenu(),
      issues: blocked
          ? const [
              LauncherIssue(severity: IssueSeverity.error, message: 'blocked'),
            ]
          : const [],
    );
  }

  @override
  Future<LaunchResult> launchSession(
    GameInstall install,
    LauncherProfile profile,
    LaunchSessionRequest session, {
    LaunchSelection? selectionOverride,
    bool restart = false,
    CancellationToken? cancellation,
    String? expectedPackageDigest,
  }) async {
    launchedDigest = expectedPackageDigest;
    cancellation?.throwIfCancelled();
    launches++;
    request = session;
    launchToken = cancellation;
    return const LaunchResult(started: true, message: 'Started');
  }

  @override
  dynamic noSuchMethod(Invocation invocation) => super.noSuchMethod(invocation);
}

void main() {
  test(
    'LAN review persists full defaults and starts V5 with consent',
    () async {
      final rig = Rig();
      final plan = await rig.prepare() as HostingPlan;
      expect(plan.joinability, contains('local network'));
      final token = CancellationToken();
      expect((await rig.start(plan, token: token)).processStarted, isTrue);
      expect(rig.launches, 1);
      expect(rig.launchedDigest, rig.digest);
      expect(identical(rig.launchToken, token), isTrue);
      expect(rig.request!.host!.name, 'Evening game');
      expect(rig.request!.host!.maxPlayers, 12);
      expect(rig.request!.host!.passwordRequired, isTrue);
      expect(rig.request!.host!.voice, isFalse);
      expect(rig.request!.transport.allowsDirect, isFalse);
      expect(rig.preferences.hosting.toJson(), plan.choices.defaults.toJson());
      expect(rig.reads, 0);
    },
  );
  for (final value in [GameRunState.running, GameRunState.unknown]) {
    test('refuses $value before synchronization or launch', () async {
      final rig = Rig()..running = value;
      expect(await rig.prepare(), isA<HostingRefused>());
      expect(rig.launches, 0);
    });
  }
  test('requires disclosure and never launches a declined review', () async {
    final rig = Rig();
    final plan = await rig.prepare() as HostingPlan;
    expect((await rig.start(plan, disclosure: false)).processStarted, isFalse);
    expect(rig.launches, 0);
    expect(rig.preferences.settings.needsPrivacyDisclosure, isTrue);
  });
  test(
    'relay failure requires a separate direct choice; it is not remembered',
    () async {
      final rig = Rig()..relay = false;
      final refused =
          await rig.prepare(
                options: rig.choices(visibility: ServerVisibility.invite),
              )
              as HostingRefused;
      expect(refused.canAllowDirect, isTrue);
      final plan =
          await rig.prepare(
                options: rig.choices(
                  visibility: ServerVisibility.invite,
                  direct: true,
                ),
              )
              as HostingPlan;
      expect((await rig.start(plan)).processStarted, isTrue);
      expect(rig.request!.transport.allowsDirect, isTrue);
      expect(
        rig.preferences.settings
            .privacyForSession(allowDirect: false)
            .allowsDirect,
        isFalse,
      );
    },
  );
  test('service failure does not fall back to direct', () async {
    final rig = Rig()..available = false;
    final result =
        await rig.prepare(
              options: rig.choices(visibility: ServerVisibility.public),
            )
            as HostingRefused;
    expect(result.canAllowDirect, isFalse);
    expect(result.message, isNot(contains('private endpoint')));
  });
  test(
    'voice, developer packages and discovery port need their prerequisites',
    () async {
      final rig = Rig();
      for (final options in [
        rig.choices(voice: true),
        rig.choices(serve: true),
        rig.choices(port: 27251),
      ]) {
        expect(await rig.prepare(options: options), isA<HostingRefused>());
      }
      expect(rig.launches, 0);
    },
  );
  test('unlaunchable profiles and unreadable settings refuse', () async {
    final rig = Rig()..blocked = true;
    expect(await rig.prepare(), isA<HostingRefused>());
    rig
      ..blocked = false
      ..syncFails = true;
    expect(await rig.prepare(), isA<HostingRefused>());
  });
  test('late cancellation after preview starts nothing', () async {
    final rig = Rig();
    final plan = await rig.prepare() as HostingPlan;
    final token = CancellationToken();
    rig.beforePreview = () async => token.cancel();
    expect((await rig.start(plan, token: token)).processStarted, isFalse);
    expect(rig.launches, 0);
  });
  test(
    'preferences or package digest changed since review require another review',
    () async {
      for (final setting in [true, false]) {
        final rig = Rig();
        final plan = await rig.prepare() as HostingPlan;
        if (setting) {
          rig.preferences = const MultiplayerPreferencesChange(
            displayName: 'Sam',
          ).applyTo(rig.preferences);
        } else {
          rig.digest = 'changed';
        }
        expect((await rig.start(plan)).processStarted, isFalse);
        expect(rig.launches, 0);
      }
    },
  );
  test(
    'a writer between recheck and save cannot overwrite reviewed defaults',
    () async {
      final rig = Rig();
      final plan = await rig.prepare() as HostingPlan;
      rig.beforeSave = () async {
        rig.preferences = const MultiplayerPreferencesChange(
          hostingPort: 12345,
        ).applyTo(rig.preferences);
      };
      expect((await rig.start(plan)).processStarted, isFalse);
      expect(rig.preferences.hosting.port, 12345);
      expect(rig.launches, 0);
    },
  );
  test('exceptions never surface paths or endpoints', () async {
    final rig = Rig()..throwPreview = true;
    final result = await rig.prepare() as HostingRefused;
    expect(result.message, isNot(contains('203.0.113.77')));
    expect(result.message, isNot(contains('private path')));
  });
}
