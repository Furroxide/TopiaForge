import 'dart:io';
import 'package:launcher_data/launcher_data.dart';
import 'package:launcher_domain/launcher_domain.dart';
import 'package:test/test.dart';
import 'package:topiaforge/src/cli_host_options.dart';
import 'package:topiaforge/src/cli_multiplayer_text.dart';
import 'cli_host_fixture.dart';

void main() {
  late HostFixture rig;
  setUp(() => rig = HostFixture());
  tearDown(() => rig.dispose());
  test('no terminal needs yes before preferences or launch', () async {
    expect(await rig.run([]), 2);
    expect(rig.syncs, 0);
    expect(rig.launches, 0);
    expect(rig.messages.single, startsWith('TFHOST110:'));
  });
  test('yes with a terminal cannot bypass consent', () async {
    rig.terminal = true;
    rig.consent = false;
    rig.onLaunch = () => rig.controller.add(rig.success());
    expect(await rig.run(), 130);
    expect(rig.prompts.single, 'I understand [y/N]');
    expect(rig.launches, 0);
    expect(rig.settings.needsPrivacyDisclosure, isTrue);
  });
  test('first-use disclosure is printed even without a terminal', () async {
    rig.onLaunch = () => rig.controller.add(rig.success());
    expect(await rig.run(), 0);
    expect(rig.messages, containsAllInOrder(CliMultiplayerText.disclosure));
    expect(rig.settings.needsPrivacyDisclosure, isFalse);
    expect(rig.request!.transport.privacy, MultiplayerAddressPrivacy.relayOnly);
    expect(rig.request!.host!.voice, isFalse);
    expect(rig.request!.transport.upnp, isFalse);
  });
  test(
    'explicit direct is scoped to one request and still asks with yes',
    () async {
      rig.terminal = true;
      rig.onLaunch = () => rig.controller.add(rig.success());
      expect(await rig.run(['--yes', '--allow-direct']), 0);
      expect(rig.prompts, contains('Allow for this session [y/N]'));
      expect(rig.messages, contains(CliMultiplayerText.direct));
      expect(rig.request!.transport.directConsent!.intentId, 'intent-one');
      expect(rig.settings.defaultPrivacy, MultiplayerAddressPrivacy.relayOnly);
    },
  );
  test('serve packages refuses ordinary mode before launch', () async {
    expect(await rig.run(['--yes', '--serve-packages']), 2);
    expect(rig.launches, 0);
  });
  test('developer host can serve packages and asks password in game', () async {
    rig.developer = true;
    rig.onLaunch = () => rig.controller.add(rig.success());
    expect(
      await rig.run([
        '--yes',
        '--serve-packages',
        '--password',
        '--name',
        'Sam',
        '--port',
        '0',
        '--max-players',
        '2',
      ]),
      0,
    );
    expect(rig.request!.host!.servePackages, isTrue);
    expect(rig.request!.host!.passwordRequired, isTrue);
    expect(rig.request!.transport.port, 0);
  });
  for (final state in [GameRunState.running, GameRunState.unknown]) {
    test('running state $state refuses preference writes', () async {
      rig.running = state;
      expect(await rig.run(), 1);
      expect(rig.syncs, 0);
    });
  }
  test('game started during consent refuses saves and launch', () async {
    rig.terminal = true;
    rig.onConsent = () => rig.running = GameRunState.running;
    expect(await rig.run(), 1);
    expect(rig.settings.needsPrivacyDisclosure, isTrue);
    expect(rig.launches, 0);
  });
  test('unreadable settings fail closed at 110', () async {
    rig.readable = false;
    expect(await rig.run(), 1);
    expect(rig.messages.last, startsWith('TFHOST110:'));
  });
  test('blocked preflight fails at 120', () async {
    rig.blocked = true;
    expect(await rig.run(), 1);
    expect(rig.messages.last, startsWith('TFHOST120:'));
    expect(rig.launches, 0);
  });
  test('unavailable Internet never falls back to direct', () async {
    rig.features = null;
    expect(await rig.run(['--yes', '--visibility', 'invite']), 1);
    expect(rig.launches, 0);
  });
  test('direct-only Internet cannot host without the explicit flag', () async {
    rig.features = const MultiplayerServiceFeatures(
      hosting: true,
      invites: true,
      signaling: true,
      relay: false,
      killSwitch: true,
      heartbeat: Duration(seconds: 10),
      listingTtl: Duration(seconds: 30),
      relayCredentialTtl: Duration(minutes: 5),
    );
    rig.onLaunch = () => rig.controller.add(rig.success());
    expect(await rig.run(['--yes', '--visibility', 'invite']), 1);
    expect(rig.launches, 0);
    expect(rig.messages.last, startsWith('TFHOST120:'));
    expect(rig.messages.last, contains('--allow-direct'));
  });
  test('spawn refusal is 130 and hides raw details', () async {
    rig.starts = false;
    expect(await rig.run(), 1);
    expect(rig.messages.last, startsWith('TFHOST130:'));
    expect(rig.messages.join(), isNot(contains('private endpoint')));
  });
  test('spawn exception is 130 and hides raw details', () async {
    rig.throwLaunch = true;
    expect(await rig.run(), 1);
    expect(rig.messages.last, startsWith('TFHOST130:'));
    expect(rig.messages.join(), isNot(contains('private endpoint')));
  });
  for (final failure in [
    LaunchMultiplayerFailure.portInUse,
    LaunchMultiplayerFailure.identityUnavailable,
    LaunchMultiplayerFailure.networkStopped,
    LaunchMultiplayerFailure.serviceUnavailable,
  ]) {
    test(
      'runtime $failure is 140 with a next step and no raw detail',
      () async {
        rig.onLaunch = () => rig.controller.add(rig.failure(failure));
        expect(await rig.run(), 1);
        expect(rig.messages.last, startsWith('TFHOST140:'));
        expect(rig.messages.join(), isNot(contains('private endpoint')));
      },
    );
  }
  for (final args in [
    ['--no-wait'],
    ['--port', '27251'],
    ['--max-players', '17'],
    ['--visibility', 'invalid'],
    ['--yes', '--yes'],
    ['--password', 'secret'],
    ['--allow-host-packages'],
    ['--port'],
    ['--wait-seconds', '0'],
  ]) {
    test('invalid host invocation ${args.first} refuses before effects', () {
      expect(() => CliHostOptions(args), throwsFormatException);
    });
  }
  test('approved disclosure and direct wording match the source', () {
    final text = File('../../docs/internal/MultiplayerPrivacyText.md')
        .readAsStringSync()
        .replaceAll(RegExp(r'\r?\n> ?'), ' ')
        .replaceAll(RegExp(r'\s+'), ' ');
    for (final line in [
      ...CliMultiplayerText.disclosure,
      CliMultiplayerText.direct,
      CliMultiplayerText.voiceOff,
      CliMultiplayerText.voiceOn,
    ]) {
      expect(text, contains(line));
    }
  });
}
