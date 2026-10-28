import 'dart:async';

import 'package:test/test.dart';
import 'package:topiaforge/src/dev_peer_supervisor.dart';

void main() {
  test('Ctrl+C stops supervision and attempts every owned peer', () async {
    final stop = Completer<void>();
    final closed = <int>[];
    final peers = List.generate(
      3,
      (index) => DevPeerHandle(
        index: index,
        alive: () async => true,
        lines: () async => ['running'],
        close: () async {
          closed.add(index);
        },
      ),
    );
    final lines = <String>[];
    await superviseDevPeers(
      peers,
      stop.future,
      lines.add,
      delay: (_) async {
        stop.complete();
      },
    );
    expect(await closeDevPeers(peers), isTrue);
    expect(closed, [2, 1, 0]);
    expect(lines, ['[peer 0] running', '[peer 1] running', '[peer 2] running']);
  });

  test(
    'a crashed peer reports its last lines while its sibling continues',
    () async {
      final stop = Completer<void>();
      var tick = 0;
      var siblingReads = 0;
      final lines = <String>[];
      final peers = [
        DevPeerHandle(
          index: 0,
          alive: () async => false,
          lines: () async => List.generate(40, (index) => 'line $index'),
          close: () async {},
        ),
        DevPeerHandle(
          index: 1,
          alive: () async => true,
          lines: () async {
            siblingReads++;
            return ['live $tick'];
          },
          close: () async {},
        ),
      ];
      await superviseDevPeers(
        peers,
        stop.future,
        lines.add,
        delay: (_) async {
          if (++tick == 3) stop.complete();
        },
      );
      expect(siblingReads, 3);
      expect(lines.where((line) => line.contains('exited;')), hasLength(1));
      expect(lines, contains('[peer 0] line 15'));
      expect(lines, isNot(contains('[peer 0] line 14')));
      expect(lines, contains('[peer 1] live 2'));
    },
  );

  test(
    'cleanup failure preserves the run and does not skip other peers',
    () async {
      final closed = <int>[];
      final peers = List.generate(
        3,
        (index) => DevPeerHandle(
          index: index,
          alive: () async => true,
          lines: () async => [],
          close: () async {
            closed.add(index);
            if (index == 1) throw StateError('identity unknown');
          },
        ),
      );
      expect(await closeDevPeers(peers), isFalse);
      expect(closed, [2, 1, 0]);
    },
  );

  test(
    'stable logs are printed once, and no-tail still reports crashes',
    () async {
      for (final tail in [false, true]) {
        final stop = Completer<void>();
        var tick = 0;
        final output = <String>[];
        final peer = DevPeerHandle(
          index: 0,
          alive: () async => tick < 2,
          lines: () async => ['stable'],
          close: () async {},
        );
        await superviseDevPeers(
          [peer],
          stop.future,
          output.add,
          tail: tail,
          delay: (_) async {
            tick++;
          },
        );
        stop.complete();
        expect(
          output.where((line) => line == '[peer 0] stable'),
          hasLength(tail ? 2 : 1),
        );
        expect(output.where((line) => line.contains('exited;')), hasLength(1));
      }
    },
  );
}
