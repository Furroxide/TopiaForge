part of '../models.dart';

class UgcPublisherStartResult {
  const UgcPublisherStartResult({
    required this.started,
    required this.message,
    this.sessionId = 0,
  });

  final bool started;
  final String message;
  final int sessionId;
}

sealed class UgcPublisherEvent {
  const UgcPublisherEvent(this.sessionId);

  final int sessionId;
}

final class UgcPublisherOutput extends UgcPublisherEvent {
  const UgcPublisherOutput(super.sessionId, this.line);

  final String line;
}

final class UgcPublisherExited extends UgcPublisherEvent {
  const UgcPublisherExited(super.sessionId, this.exitCode);

  final int exitCode;
}
