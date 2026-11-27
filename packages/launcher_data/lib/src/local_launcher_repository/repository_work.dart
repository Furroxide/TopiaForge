part of '../local_launcher_repository.dart';

/// What the repository is doing, so that closing it waits for its work.
///
/// Every public operation runs through [_tracked]. Until an operation ends it
/// can hold files open in the game's and the launcher's folders, the
/// install's staging lease among them, and write there. So [_close] waits for
/// each one, work started while it waits included: a launch stops at its
/// next step, a join's downloads stop at once, and anything else finishes.
/// Once the close completes, nothing started before then still runs or holds
/// a file open, and the folders it used can be moved or deleted. Work started
/// after that still runs, and nothing waits for it.
extension _RepositoryWork on LocalLauncherRepository {
  /// Starts [operation], kept in [_inFlight] until it ends.
  Future<T> _tracked<T>(Future<T> Function() operation) {
    final running = operation();
    _inFlight.add(running);
    running.whenComplete(() => _inFlight.remove(running)).ignore();
    return running;
  }

  /// Closes the repository once nothing it started still runs, then ends
  /// launch monitoring and the activity stream. A hook an operation calls
  /// must not wait for the close, which would then wait for that operation.
  ///
  /// A join's downloads are cancelled as the close begins, so it never waits
  /// out a transfer: a cancelled download deletes its temporary file before
  /// it returns, which keeps the close's promise that nothing stays open.
  Future<void> _close() async {
    _disposed = true;
    _closing.cancel();
    await _closeHandoffs();
    while (_inFlight.isNotEmpty) {
      // Each operation reports its outcome to its own caller; the close
      // needs only its end.
      await Future.wait([
        for (final running in _inFlight)
          running.then<void>((_) {}, onError: (Object _) {}),
      ]);
    }
    for (final monitor in _launchMonitors.values) {
      await monitor.dispose();
    }
    _launchMonitors.clear();
    await _launchActivities.close();
  }

  /// A token that [caller]'s token and the repository's close both cancel,
  /// with the registrations that link them; dispose those when the work
  /// ends.
  (CancellationToken, List<CancellationRegistration>) _linkedToClose(
    CancellationToken? caller,
  ) {
    final linked = CancellationToken();
    final registrations = [
      ?caller?.register(linked.cancel),
      _closing.register(linked.cancel),
    ];
    return (linked, registrations);
  }
}
