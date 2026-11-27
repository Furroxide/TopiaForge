/// A one-shot request to stop long work, shared by the launcher's domain
/// interfaces, the HTTPS helpers and the multiplayer clients.
///
/// It lives in the domain so that interfaces such as
/// `MultiplayerContentHost` can take one; it holds no I/O of its own.
library;

/// Thrown by work that stopped because its [CancellationToken] was cancelled.
final class OperationCancelledException implements Exception {
  const OperationCancelledException([
    this.message = 'The operation was cancelled.',
  ]);

  final String message;

  @override
  String toString() => 'OperationCancelledException: $message';
}

/// Asks work to stop. Cancelling is idempotent and synchronous: every
/// registered callback runs once, in registration order, before [cancel]
/// returns, and work that checks [isCancelled] before contacting anything
/// contacts nothing.
///
/// A token belongs to one piece of work, such as one join or one fetch; the
/// work disposes its [CancellationRegistration]s when it ends, so a token that
/// is never cancelled holds nothing for long.
final class CancellationToken {
  CancellationToken();

  final Map<int, void Function()> _callbacks = {};
  var _nextId = 0;
  var _cancelled = false;

  bool get isCancelled => _cancelled;

  void cancel() {
    if (_cancelled) return;
    _cancelled = true;
    final callbacks = [..._callbacks.values];
    _callbacks.clear();
    for (final callback in callbacks) {
      try {
        callback();
      } on Object {
        // One failing listener must not keep the others from stopping.
      }
    }
  }

  /// Throws [OperationCancelledException] once cancelled.
  void throwIfCancelled() {
    if (_cancelled) throw const OperationCancelledException();
  }

  /// Runs [callback] once when this token is cancelled, or at once when it
  /// already is. Dispose the registration when the work ends.
  CancellationRegistration register(void Function() callback) {
    if (_cancelled) {
      callback();
      return const CancellationRegistration._(null, 0);
    }
    final id = _nextId++;
    _callbacks[id] = callback;
    return CancellationRegistration._(this, id);
  }
}

/// A callback registered with [CancellationToken.register].
final class CancellationRegistration {
  const CancellationRegistration._(this._token, this._id);

  final CancellationToken? _token;
  final int _id;

  /// Forgets the callback; harmless after it ran or was disposed.
  void dispose() => _token?._callbacks.remove(_id);
}
