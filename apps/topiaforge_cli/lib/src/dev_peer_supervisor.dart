import 'dart:async';

/// The supervisor sees only owned peer operations, never process names or PIDs.
final class DevPeerHandle {
  const DevPeerHandle({
    required this.index,
    required this.alive,
    required this.lines,
    required this.close,
    this.launchErrorLogged = _noLaunchError,
  });
  final int index;
  final Future<bool?> Function() alive;
  final Future<List<String>> Function() lines;
  final Future<void> Function() close;

  /// True when this peer's launch was refused and its full error went to the
  /// peer's own log inside the run folder.
  final bool Function() launchErrorLogged;
}

bool _noLaunchError() => false;

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

/// What happened to a development run's folder when the run ended.
enum DevPeerRunFolder {
  /// Every peer stopped and nothing needed the folder; it was removed.
  removed,

  /// A peer's launch was refused; the folder holds the log its error names.
  keptForLaunchError,

  /// A peer could not be confirmed stopped; the folder is kept for recovery.
  keptUnconfirmed,
}

/// Closes every peer, then calls [removeRun] only when every close was
/// confirmed and no peer's refused launch wrote its error into the run folder.
Future<DevPeerRunFolder> endDevPeerRun(
  List<DevPeerHandle> peers,
  void Function() removeRun,
) async {
  if (!await closeDevPeers(peers)) return DevPeerRunFolder.keptUnconfirmed;
  if (peers.any((peer) => peer.launchErrorLogged())) {
    return DevPeerRunFolder.keptForLaunchError;
  }
  removeRun();
  return DevPeerRunFolder.removed;
}
