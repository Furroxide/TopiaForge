import 'dart:async';

/// The supervisor sees only owned peer operations, never process names or PIDs.
final class DevPeerHandle {
  const DevPeerHandle({
    required this.index,
    required this.alive,
    required this.lines,
    required this.close,
  });
  final int index;
  final Future<bool?> Function() alive;
  final Future<List<String>> Function() lines;
  final Future<void> Function() close;
}

Future<void> superviseDevPeers(
  List<DevPeerHandle> peers,
  Future<void> stop,
  void Function(String) write, {
  bool tail = true,
  Future<void> Function(Duration)? delay,
}) async {
  final wait = delay ?? Future<void>.delayed;
  var stopped = false;
  unawaited(stop.then((_) => stopped = true));
  final crashed = <int>{};
  final previous = <int, List<String>>{};
  while (!stopped && crashed.length != peers.length) {
    for (final peer in peers) {
      if (crashed.contains(peer.index)) continue;
      final alive = await peer.alive();
      final lines = await peer.lines();
      if (alive == false) {
        crashed.add(peer.index);
        write(
          '[peer ${peer.index}] exited; other peers continue. Last log lines:',
        );
        for (final line in lines.skip(
          lines.length > 25 ? lines.length - 25 : 0,
        )) {
          write('[peer ${peer.index}] $line');
        }
      } else if (tail) {
        final old = previous[peer.index] ?? const <String>[];
        var overlap = old.length < lines.length ? old.length : lines.length;
        while (overlap > 0) {
          var matches = true;
          for (var index = 0; index < overlap; index++) {
            if (old[old.length - overlap + index] != lines[index]) {
              matches = false;
              break;
            }
          }
          if (matches) break;
          overlap--;
        }
        for (final line in lines.skip(overlap)) {
          write('[peer ${peer.index}] $line');
        }
      }
      previous[peer.index] = lines;
    }
    if (!stopped && crashed.length != peers.length) {
      await Future.any([stop, wait(const Duration(milliseconds: 500))]);
    }
  }
}

/// All owned processes get a stop attempt even when one cannot be identified.
/// The caller retains the overlay whenever any close remains unconfirmed.
Future<bool> closeDevPeers(List<DevPeerHandle> peers) async {
  var confirmed = true;
  for (final peer in peers.reversed) {
    try {
      await peer.close();
    } on Object {
      confirmed = false;
    }
  }
  return confirmed;
}
