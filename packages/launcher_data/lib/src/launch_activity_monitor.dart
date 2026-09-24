import 'dart:async';

import 'package:launcher_domain/launcher_domain.dart';

import 'launch_staging_store.dart';

/// Reads one request's runtime evidence without turning a process handle into success.
final class LaunchActivityMonitor {
  LaunchActivityMonitor({
    required this.store,
    required this.current,
    required this.onChanged,
    this.processAlive,
    this.pollInterval = const Duration(milliseconds: 250),
    this.acknowledgementTimeout = const Duration(seconds: 30),
    Duration Function()? elapsed,
  }) : _elapsed = elapsed ?? (Stopwatch()..start()).elapsedGetter;
  final LaunchStagingStore store;
  LaunchActivity current;
  final void Function(LaunchActivity) onChanged;
  final Future<bool?> Function(LaunchProcessIdentity)? processAlive;
  final Duration pollInterval;
  final Duration acknowledgementTimeout;
  final Duration Function() _elapsed;
  Timer? _timer;
  Future<void>? _polling;
  bool _disposed = false;
  bool _finished = false;

  bool get finished => _finished;
  Future<void> start() async {
    if (_disposed || _timer != null || _finished) return;
    await pollOnce();
    if (!_disposed && !_finished) {
      _timer = Timer.periodic(pollInterval, (_) {
        unawaited(pollOnce());
      });
    }
  }

  Future<void> pollOnce() {
    if (_disposed || _finished) return Future<void>.value();
    return _polling ??= _poll().whenComplete(() {
      _polling = null;
    });
  }

  Future<void> _poll() async {
    var next = await _readEvidence(current);
    if (_disposed) return;
    if (!next.acknowledged &&
        _elapsed() >= acknowledgementTimeout &&
        !next.unconfirmed) {
      next = next.copyWith(unconfirmed: true);
    }
    if (next.process != null && processAlive != null) {
      bool? alive;
      try {
        alive = await processAlive!(next.process!);
      } on Object {
        alive = null;
      }
      if (_disposed) return;
      if (alive == false) {
        // Once absence is verified the producer cannot write again. Read both
        // outcome channels once more before closing this request's monitor.
        next = await _readEvidence(next);
        if (_disposed) return;
        next = next.copyWith(
          processExited: true,
          unconfirmed:
              next.launchOutcome == null ||
              next.command == 'launch-target' && next.sessionOutcome == null,
        );
        _finished = true;
      }
    }
    if (next.sessionOutcome != null && next.launchOutcome != null ||
        next.command == 'main-menu' && next.launchOutcome != null ||
        next.launchOutcome?.status != null &&
            next.launchOutcome!.status != 'succeeded' &&
            next.launchOutcome!.sessionId == null) {
      _finished = true;
    }
    if (!identical(next, current)) {
      current = next;
      try {
        onChanged(next);
      } on Object {
        /* Consumer failure cannot terminate evidence collection. */
      }
    }
    if (_finished) _timer?.cancel();
  }

  Future<LaunchActivity> _readEvidence(LaunchActivity next) async {
    final progress = await store.readProgress(next.requestId);
    final launch = await store.readOutcome(next.requestId, session: false);
    final terminal = await store.readOutcome(next.requestId, session: true);
    if (progress != null) next = next.applyProgress(progress);
    if (launch != null) next = next.applyOutcome(launch);
    if (terminal != null) next = next.applyOutcome(terminal);
    return next;
  }

  Future<void> dispose() async {
    _disposed = true;
    _timer?.cancel();
    await _polling;
  }
}

extension on Stopwatch {
  Duration Function() get elapsedGetter =>
      () => elapsed;
}
