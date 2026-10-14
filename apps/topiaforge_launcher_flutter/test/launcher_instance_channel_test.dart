import 'dart:async';

import 'package:flutter/services.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:launcher_domain/launcher_domain.dart';
import 'package:topiaforge_launcher_flutter/src/launcher_instance.dart';
import 'package:topiaforge_launcher_flutter/src/launcher_instance_channel.dart';
import 'package:topiaforge_launcher_flutter/src/session_link_inbox.dart';

/// The Dart half of the runner contract: the calls Dart makes, and the calls
/// the runner makes into Dart, over the real method channel codec.
void main() {
  TestWidgetsFlutterBinding.ensureInitialized();
  const channel = MethodChannel(LauncherInstanceChannel.name);
  final messenger =
      TestDefaultBinaryMessengerBinding.instance.defaultBinaryMessenger;
  late SessionLinkInbox inbox;
  late List<SessionLinkArrival> arrivals;
  late StreamSubscription<SessionLinkArrival> subscription;
  late LauncherInstanceChannel instance;
  late List<MethodCall> calls;

  void runner(Object? Function(MethodCall call) answer) {
    messenger.setMockMethodCallHandler(channel, (call) async {
      calls.add(call);
      return answer(call);
    });
  }

  /// Delivers [method] from the runner, as WM_COPYDATA or D-Bus would.
  Future<ByteData?> fromRunner(String method, Object? argument) {
    final completer = Completer<ByteData?>();
    messenger.handlePlatformMessage(
      LauncherInstanceChannel.name,
      const StandardMethodCodec().encodeMethodCall(
        MethodCall(method, argument),
      ),
      completer.complete,
    );
    return completer.future;
  }

  setUp(() {
    calls = [];
    arrivals = [];
    inbox = SessionLinkInbox();
    subscription = inbox.arrivals.listen(arrivals.add);
    instance = LauncherInstanceChannel(inbox: inbox);
  });

  tearDown(() async {
    messenger.setMockMethodCallHandler(channel, null);
    await subscription.cancel();
  });

  test('claim sends the scope and reads the role', () async {
    for (final (answer, claim) in [
      ('primary', LauncherInstanceClaim.primary),
      ('secondary', LauncherInstanceClaim.secondary),
      ('something else', LauncherInstanceClaim.notSupported),
      (null, LauncherInstanceClaim.notSupported),
    ]) {
      runner((_) => answer);
      expect(await instance.claim('ab' * 16), claim);
    }
    expect(calls.first.method, 'claim');
    expect(calls.first.arguments, {'scope': 'ab' * 16});
  });

  test('a platform without the runner side claims nothing', () async {
    expect(await instance.claim('ab' * 16), LauncherInstanceClaim.notSupported);
    expect(await instance.handOff('topiaforge://join/ABCDE12345'), isFalse);
    await instance.ready();
  });

  test('a runner error counts as no claim and no hand-off', () async {
    runner((_) => throw PlatformException(code: 'broken'));
    expect(await instance.claim('ab' * 16), LauncherInstanceClaim.notSupported);
    expect(await instance.handOff(null), isFalse);
  });

  test('hand-off sends the link, or null to only come forward', () async {
    runner((_) => true);
    expect(await instance.handOff('topiaforge://join/ABCDE12345'), isTrue);
    expect(await instance.handOff(null), isTrue);
    expect(calls.map((call) => call.arguments), [
      {'link': 'topiaforge://join/ABCDE12345'},
      {'link': null},
    ]);
    runner((_) => null);
    expect(await instance.handOff(null), isFalse);
  });

  test('ready tells the runner that Dart listens', () async {
    runner((_) => null);
    await instance.ready();
    expect(calls.single.method, 'ready');
  });

  test('a link from the runner reaches the inbox as forwarded text', () async {
    await fromRunner('link', 'topiaforge://join/ABCDE12345');
    await pumpEventQueue();

    expect(arrivals, hasLength(1));
    expect(arrivals.single.text, 'topiaforge://join/ABCDE12345');
    expect(arrivals.single.source, SessionLinkSource.forwarded);
    expect(arrivals.single.read().link?.inviteCode, 'ABCDE12345');
  });

  test('a link the runner refused arrives with its reason only', () async {
    await fromRunner('linkRefused', 'tooLong');
    await fromRunner('linkRefused', 'invalid');
    await pumpEventQueue();

    expect(arrivals.map((arrival) => arrival.text), [null, null]);
    expect(arrivals.map((arrival) => arrival.read().refusal), [
      SessionLinkRefusal.tooLong,
      SessionLinkRefusal.invalid,
    ]);
  });

  test(
    'anything else from the runner is refused and delivers nothing',
    () async {
      final reply = await fromRunner('link', 42);
      await fromRunner('shell', 'cmd /c calc');
      await pumpEventQueue();

      expect(reply, isNull);
      expect(arrivals, isEmpty);
    },
  );
}
