import 'dart:convert';
import 'package:launcher_domain/launcher_domain.dart';

/// Both languages perform the same bounded strict string reader/writer operation.
Map<String, Object?> transportSnapshot(Map<String, Object?> body) {
  String roundTrip(String value) => switch (body['transport']) {
    'plan' => LaunchTransport.writePlan(LaunchTransport.readPlan(value)),
    'profile' => LaunchTransport.writeProfile(
      LaunchTransport.readProfile(value),
    ),
    'progress' => LaunchTransport.writeProgress(
      LaunchTransport.readProgress(value),
    ),
    'outcome' => LaunchTransport.writeOutcome(
      LaunchTransport.readOutcome(value),
    ),
    'observation' => LaunchTransport.writeObservation(
      LaunchTransport.readObservation(value),
    ),
    _ => throw StateError('Unknown transport ${body['transport']}'),
  };
  try {
    final first = roundTrip(
      (body['wireJson'] as String?) ?? jsonEncode(body['payload']),
    );
    if (first != roundTrip(first)) {
      throw StateError('Transport normalization is not stable.');
    }
    return {'outcome': 'accept', 'normalized': jsonDecode(first)};
  } on FormatException {
    return {
      'outcome': 'reject',
      'errorCodes': ['transport'],
    };
  }
}
