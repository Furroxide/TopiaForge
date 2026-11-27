/// How fast the launcher may ask the TopiaForge service for anything, so it
/// never sends faster than the service's limits allow
/// (`docs/internal/ControlPlaneApi.md`, section 1).
library;

import 'dart:math' as math;

/// The kinds of request the launcher sends, each paced on its own, as the
/// service limits them.
enum ControlPlaneScope {
  /// `GET /v1/status`, which the service does not limit.
  status,

  /// `GET /v1/servers`, and `GET /v1/servers/{id}` for the browser's detail
  /// pane: server browsing.
  browse,

  /// `GET /v1/servers/{id}` for a join of a server link or an Internet
  /// listing. The service limits only the list, and that limit is shared by
  /// every launcher on the network, so joins count apart from browsing:
  /// neither browsing nor a delay the list was given ever holds a join back.
  join,

  /// `POST /v1/invites/resolve`, whose misses the service limits.
  invite,
}

/// Why a scope must wait before its next request.
enum ControlPlaneHold {
  /// The service named a delay with a 429. Its limits count every launcher
  /// on the network, not only this one.
  rateLimited,

  /// This launcher's own count of recent requests reached the service's
  /// limit.
  paced,

  /// The service failed, or named a delay while it was unavailable, and is
  /// given time to recover.
  unavailable,
}

/// One limit: at most [limit] requests in any [length].
final class ControlPlaneWindow {
  const ControlPlaneWindow(this.length, this.limit);

  final Duration length;
  final int limit;
}

/// A request a scope counted when it went out. An invite resolution keeps
/// its slot only when it missed, as the service counts misses.
final class ControlPlaneSlot {
  ControlPlaneSlot._(this.scope, this.at);

  final ControlPlaneScope scope;
  final DateTime at;
}

/// Paces the launcher's requests to the service, scope by scope.
///
/// - **The service's limits.** Server browsing may make 60 requests a minute
///   and 1,200 an hour, and invite resolutions may miss 10 times a minute and
///   30 an hour. Joins' server details, which the service does not limit, are
///   held to browsing's limits apart from it, so a caller that loops can never
///   flood them. The service counts sliding windows: a window's own requests
///   plus a share of the previous window's. This pacer counts every request of
///   the last two windows against one window's limit, which can never pass the
///   service's estimate, however its windows fall against this clock. An invite
///   resolution holds a slot while it runs, as the service holds one, and gives
///   it back unless it missed.
/// - **Named delays.** A `Retry-After` holds its scope for that long.
/// - **Failures.** Each 5xx in a row, or answer that never came, holds the
///   scope for twice as long as the one before: one second, then two, up to
///   five minutes. A request that is answered at all ends the run.
///
/// Nothing here sleeps: a caller asks [holdFor] and decides.
final class ControlPlanePacer {
  ControlPlanePacer({DateTime Function()? clock})
    : _clock = clock ?? DateTime.now;

  /// The service's limits, as this pacer applies them.
  static const windows = <ControlPlaneScope, List<ControlPlaneWindow>>{
    ControlPlaneScope.status: [],
    ControlPlaneScope.browse: [
      ControlPlaneWindow(Duration(minutes: 1), 60),
      ControlPlaneWindow(Duration(hours: 1), 1200),
    ],
    ControlPlaneScope.join: [
      ControlPlaneWindow(Duration(minutes: 1), 60),
      ControlPlaneWindow(Duration(hours: 1), 1200),
    ],
    ControlPlaneScope.invite: [
      ControlPlaneWindow(Duration(minutes: 1), 10),
      ControlPlaneWindow(Duration(hours: 1), 30),
    ],
  };

  static const firstBackoff = Duration(seconds: 1);
  static const longestBackoff = Duration(minutes: 5);

  final DateTime Function() _clock;
  final _slots = {
    for (final scope in ControlPlaneScope.values) scope: <ControlPlaneSlot>[],
  };
  final _named = <ControlPlaneScope, (DateTime, ControlPlaneHold)>{};
  final _backoff = <ControlPlaneScope, DateTime>{};
  final _failures = <ControlPlaneScope, int>{};

  DateTime get now => _clock();

  /// How long [scope] must wait before it sends, and why; null when it may
  /// send now.
  (Duration, ControlPlaneHold)? holdFor(ControlPlaneScope scope) {
    final now = _clock();
    _prune(scope, now);
    (Duration, ControlPlaneHold)? hold;
    if (_named[scope] case (
      final until,
      final reason,
    ) when until.isAfter(now)) {
      hold = (until.difference(now), reason);
    }
    if (_backoff[scope] case final until? when until.isAfter(now)) {
      final wait = until.difference(now);
      if (hold == null || wait > hold.$1) {
        hold = (wait, ControlPlaneHold.unavailable);
      }
    }
    final slots = _slots[scope]!;
    for (final window in windows[scope]!) {
      final span = window.length * 2;
      final recent = [
        for (final slot in slots)
          if (now.difference(slot.at) < span) slot,
      ];
      if (recent.length < window.limit) continue;
      // The slot that must leave the span before one more fits.
      final leaving = recent[recent.length - window.limit];
      final wait = leaving.at.add(span).difference(now);
      if (hold == null || wait > hold.$1) {
        hold = (wait, ControlPlaneHold.paced);
      }
    }
    return hold;
  }

  /// Counts a request [scope] sends now.
  ControlPlaneSlot take(ControlPlaneScope scope) {
    final slot = ControlPlaneSlot._(scope, _clock());
    if (windows[scope]!.isNotEmpty) _slots[scope]!.add(slot);
    return slot;
  }

  /// Gives back [slot]: its request did not count against the service's
  /// limit, as an invite resolution that found its server does not.
  void giveBack(ControlPlaneSlot slot) =>
      _slots[slot.scope]!.removeWhere((held) => identical(held, slot));

  /// The service named [delay] before [scope] may ask again. A named delay
  /// is always honoured in full, whatever answers come meanwhile.
  void holdOff(ControlPlaneScope scope, Duration delay, ControlPlaneHold why) {
    final until = _clock().add(delay);
    final current = _named[scope];
    if (current == null || until.isAfter(current.$1)) {
      _named[scope] = (until, why);
    }
  }

  /// A request of [scope] failed on the service's side, or its answer never
  /// came; returns how long the scope now waits.
  Duration failed(ControlPlaneScope scope) {
    final failures = (_failures[scope] ?? 0) + 1;
    _failures[scope] = failures;
    final backoff = Duration(
      milliseconds: math
          .min(
            firstBackoff.inMilliseconds *
                math.pow(2, math.min(failures - 1, 20)),
            longestBackoff.inMilliseconds,
          )
          .toInt(),
    );
    _backoff[scope] = _clock().add(backoff);
    return backoff;
  }

  /// A request of [scope] was answered, so the service is up: its run of
  /// failures, and the wait they set, end. A delay it named still holds.
  void answered(ControlPlaneScope scope) {
    _failures.remove(scope);
    _backoff.remove(scope);
  }

  void _prune(ControlPlaneScope scope, DateTime now) {
    final longest = windows[scope]!.fold(
      Duration.zero,
      (longest, window) =>
          window.length * 2 > longest ? window.length * 2 : longest,
    );
    _slots[scope]!.removeWhere((slot) => now.difference(slot.at) >= longest);
  }
}
