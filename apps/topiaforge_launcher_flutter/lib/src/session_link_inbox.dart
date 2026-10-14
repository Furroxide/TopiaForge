import 'dart:async';

import 'package:launcher_domain/launcher_domain.dart';

/// Where a session link reached the launcher.
enum SessionLinkSource {
  /// The launcher's own command line, at start.
  commandLine,

  /// While the launcher runs: handed over by a later start of the launcher,
  /// or opened by the system (macOS).
  forwarded,
}

/// A session link as it reached the launcher, before the domain reads it.
/// [text] is untrusted input from another program.
final class SessionLinkArrival {
  const SessionLinkArrival(String this.text, {required this.source})
    : refusal = null;

  /// A link the runner refused before reading it, as one too long to carry.
  const SessionLinkArrival.refused(
    SessionLinkRefusal this.refusal, {
    required this.source,
  }) : text = null;

  final String? text;
  final SessionLinkRefusal? refusal;
  final SessionLinkSource source;

  /// The arrival read by the domain's rules.
  SessionLinkReading read() {
    final refused = refusal;
    return refused == null
        ? SessionLink.read(text)
        : SessionLinkReading.refused(refused);
  }

  /// Never the text: a direct link holds an address.
  @override
  String toString() => 'session link arrival (${source.name})';
}

/// Holds the newest session link that has not reached the launcher yet.
///
/// Until something listens, a newer link replaces the one held, so links
/// never queue up. Once [arrivals] is listened to, every link passes
/// straight through.
final class SessionLinkInbox {
  SessionLinkInbox() {
    _controller = StreamController<SessionLinkArrival>.broadcast(
      onListen: _release,
    );
  }

  late final StreamController<SessionLinkArrival> _controller;
  SessionLinkArrival? _held;

  Stream<SessionLinkArrival> get arrivals => _controller.stream;

  void deliver(SessionLinkArrival arrival) {
    if (_controller.isClosed) return;
    if (_controller.hasListener) {
      _controller.add(arrival);
    } else {
      _held = arrival;
    }
  }

  Future<void> close() => _controller.close();

  void _release() {
    final held = _held;
    _held = null;
    if (held != null) _controller.add(held);
  }
}
