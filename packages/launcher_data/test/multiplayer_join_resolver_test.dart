import 'dart:async';
import 'dart:convert';
import 'dart:io';

import 'package:launcher_data/launcher_data.dart';
import 'package:launcher_data/src/multiplayer/descriptor_client.dart';
import 'package:launcher_data/src/multiplayer/join_descriptor_resolver.dart';
import 'package:launcher_domain/launcher_domain.dart';
import 'package:path/path.dart' as p;
import 'package:test/test.dart';

import 'multiplayer_network_support.dart';

/// The resolver's one budget across a name's addresses, how it reads a
/// missing route, and the endpoint statuses it reports as the game does.
void main() {
  late Directory root;

  setUp(() => root = Directory.systemTemp.createTempSync('tf-mp-resolve-'));
  tearDown(() => root.deleteSync(recursive: true));

  final thirdLanHost = InternetAddress('192.168.1.22');
  final byName = JoinAddressTarget(host: 'game.local', port: 27250);

  JoinDescriptorResolver resolver({
    required DescriptorClient descriptors,
    List<InternetAddress>? resolved,
    NetworkPresence? networkPresence,
    DescriptorTimer? timer,
  }) => JoinDescriptorResolver(
    descriptors: descriptors,
    store: MultiplayerStore(p.join(root.path, 'multiplayer')),
    lookup: (host) async => resolved ?? [lanHost, otherLanHost, thirdLanHost],
    networkPresence:
        networkPresence ??
        () async => throw StateError('No presence check expected.'),
    timer: timer,
  );

  JoinDescriptorRequest request(JoinTarget target) => JoinSession.start(
    target,
    privacy: SessionPrivacy.relayOnly,
  ).descriptorRequest!;

  /// A client whose connections never open, with its timers in hand.
  DescriptorClient hanging(
    List<InternetAddress> connects,
    ManualTimers timers,
  ) => DescriptorClient(
    timer: timers.call,
    connect: (address, _) {
      connects.add(address);
      return Completer<ConnectionTask<Socket>>().future;
    },
  );

  /// A client whose every connection fails with [code], or [codeFor]'s.
  DescriptorClient failing({
    int? code,
    int Function(InternetAddress)? codeFor,
  }) => DescriptorClient(
    connect: (address, port) async => throw SocketException(
      'Connection failed to $address:$port',
      osError: OSError('failed', codeFor?.call(address) ?? code!),
      address: address,
      port: port,
    ),
  );

  group('one budget for every address of a name', () {
    test('ends the fetch in flight and tries no further address', () async {
      final connects = <InternetAddress>[];
      final fetchTimers = ManualTimers();
      final budget = ManualTimers();
      var answered = false;
      final answering = resolver(
        descriptors: hanging(connects, fetchTimers),
        timer: budget.call,
      ).answer(request(byName));
      unawaited(answering.then((_) => answered = true));
      await pumpEventQueue();
      expect(budget.timers.single.delay, JoinDescriptorResolver.fetchBudget);
      expect(connects, [lanHost]);

      // The first address never connects, and its own deadline moves on.
      fetchTimers.fireLatest();
      await pumpEventQueue();
      expect(connects, [lanHost, otherLanHost]);

      // The budget ends while the second still connects.
      budget.fireLatest();
      await pumpEventQueue();
      expect(answered, isTrue);
      final event = await answering as JoinFailed;
      expect(event.code, JoinFailureCode.unreachable);
      expect(event.detail, contains('did not answer in time'));
      expect(connects, [lanHost, otherLanHost]);
      expect(fetchTimers.timers.where((timer) => timer.isActive), isEmpty);
    });

    test('is stopped by cancelling, between addresses or during one', () async {
      final connects = <InternetAddress>[];
      final fetchTimers = ManualTimers();
      final budget = ManualTimers();
      final cancellation = CancellationToken();
      final answering = resolver(
        descriptors: hanging(connects, fetchTimers),
        timer: budget.call,
      ).answer(request(byName), cancellation: cancellation);
      await pumpEventQueue();
      fetchTimers.fireLatest();
      await pumpEventQueue();
      expect(connects, [lanHost, otherLanHost]);
      cancellation.cancel();
      expect(await answering, isNull);
      expect(connects, [lanHost, otherLanHost]);
      expect(budget.timers.single.isActive, isFalse);
      expect(fetchTimers.timers.where((timer) => timer.isActive), isEmpty);
    });

    test('lets a later address answer within it', () async {
      final endpoint = await LoopbackDescriptorHost.serving(hostADescriptor);
      addTearDown(endpoint.close);
      final connects = <InternetAddress>[];
      final descriptors = DescriptorClient(
        connect: (address, port) {
          connects.add(address);
          if (address == lanHost) throw const SocketException('refused');
          return Socket.startConnect(InternetAddress.loopbackIPv4, port);
        },
      );
      final event =
          await resolver(
            descriptors: descriptors,
            resolved: [lanHost, InternetAddress.loopbackIPv4],
          ).answer(
            request(JoinAddressTarget(host: 'game.local', port: endpoint.port)),
          );
      expect(event, isA<JoinDescriptorVerified>());
      expect(connects, [lanHost, InternetAddress.loopbackIPv4]);
    });
  });

  group('no route to a host', () {
    test('is unreachable while a network is up, as for IPv6 on a network '
        'with IPv4 only', () async {
      var checks = 0;
      final event =
          await resolver(
                descriptors: failing(code: 10051),
                networkPresence: () async {
                  checks++;
                  return true;
                },
              ).answer(request(JoinAddressTarget(host: 'fd00::7', port: 27250)))
              as JoinFailed;
      expect(event.code, JoinFailureCode.unreachable);
      expect(event.detail, contains('no route'));
      expect(checks, 1);
    });

    test('is offline only when every address had none and no network is '
        'up', () async {
      for (final (up, code) in [
        (true, JoinFailureCode.unreachable),
        (false, JoinFailureCode.offline),
      ]) {
        final event =
            await resolver(
                  descriptors: failing(code: 10051),
                  resolved: [lanHost, InternetAddress('fe80::1')],
                  networkPresence: () async => up,
                ).answer(request(byName))
                as JoinFailed;
        expect(event.code, code, reason: 'network up: $up');
        for (final address in addressTexts) {
          expect(event.detail, isNot(contains(address)));
        }
      }
    });

    test('loses to any other failure, with no check', () async {
      final event =
          await resolver(
                descriptors: failing(
                  codeFor: (address) => address == lanHost ? 10051 : 10061,
                ),
                resolved: [lanHost, otherLanHost],
              ).answer(request(byName))
              as JoinFailed;
      expect(event.code, JoinFailureCode.unreachable);
      expect(event.detail, 'The host could not be reached.');
    });

    test('is not offline when the check itself fails', () async {
      final event =
          await resolver(
                descriptors: failing(code: 10050),
                resolved: [lanHost],
                networkPresence: () async =>
                    throw const SocketException('interfaces unreadable'),
              ).answer(request(byName))
              as JoinFailed;
      expect(event.code, JoinFailureCode.unreachable);
    });
  });

  test('every status but 200 is unreachable, as the game reports it', () async {
    var answer = '';
    final endpoint = await LoopbackDescriptorHost.start(
      (socket, _) => LoopbackDescriptorHost.raw(socket, latin1.encode(answer)),
    );
    addTearDown(endpoint.close);
    final typed = JoinAddressTarget(host: '127.0.0.1', port: endpoint.port);
    for (final (line, code) in [
      ('TFD1 400\n', JoinFailureCode.unreachable),
      ('TFD1 999\n', JoinFailureCode.unreachable),
      ('TFD1 200\n', JoinFailureCode.unreachable),
      ('TFD1 404\n', JoinFailureCode.unreachable),
      ('TFD1 503\n', JoinFailureCode.unreachable),
      ('TFD1 429\n', JoinFailureCode.rateLimited),
      ('TFD1 4040\n', JoinFailureCode.descriptorInvalid),
    ]) {
      answer = line;
      final event =
          await resolver(descriptors: DescriptorClient()).answer(request(typed))
              as JoinFailed;
      expect(event.code, code, reason: line);
      expect(
        JoinSession.failuresByStage[JoinStage.fetchingDescriptor],
        contains(event.code),
      );
    }
  });
}
