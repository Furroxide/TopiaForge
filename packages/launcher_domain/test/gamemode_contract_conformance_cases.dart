// Case bodies for the cross-language gamemode-contract fixtures. Kept out of the
// test file so both stay under the 500-line non-generated Dart cap, which applies
// to tests as well: the audit globs `git ls-files '*.dart'` with no exclusion.
//
// Not named `*_test.dart`, so `dart test` does not pick it up as a suite of its
// own; the runner in gamemode_contract_conformance_test.dart imports it.

import 'package:launcher_domain/launcher_domain.dart';

/// What one runner did with one fixture. `errorCodes` is compared as a set, not
/// as a count or a prefix -- comparing only the accept/reject verdict is what let
/// the two hand-written readers drift in the first place.
class ConformanceOutcome {
  const ConformanceOutcome(this.accepted, this.errorCodes, {this.detail = ''});

  const ConformanceOutcome.accepted({String detail = ''})
    : this(true, const <String>{}, detail: detail);

  final bool accepted;
  final Set<String> errorCodes;

  /// Extra context for the failure message. Never compared.
  final String detail;
}

/// The launcher writes the one-shot launch intent and the manager reads it, so
/// the contract worth pinning is producer-to-consumer: this side asserts that a
/// stored selection serializes to exactly the bytes the C# reader is fixtured
/// against, and the C# runner asserts that it accepts them.
ConformanceOutcome runLaunchIntentRoundTrip(Map<String, Object?> body) {
  final selection = body['selection']! as Map<String, Object?>;
  final expected = body['intent']! as Map<String, Object?>;

  final WorldSelection restored;
  try {
    restored = WorldSelection.fromJson(selection);
  } on FormatException catch (error) {
    return ConformanceOutcome(
      false,
      const {'selection-rejected'},
      detail: 'WorldSelection.fromJson rejected the stored selection: $error',
    );
  }

  final emitted = restored.toLaunchIntentJson();
  if (!deepEquals(emitted, expected)) {
    return ConformanceOutcome(
      false,
      const {'intent-mismatch'},
      detail:
          'the writer emitted $emitted, but the reader is fixtured '
          'against $expected',
    );
  }
  return const ConformanceOutcome.accepted();
}

/// A hostile intent is one the C# reader must defend against. This side has no
/// reader for the wire intent at all, so its obligation is the complementary
/// one: prove the launcher's own writer can never emit this shape.
///
/// The proof drives the real reader and the real writer rather than restating
/// their rules, so a change that made the writer emit one of these -- dropping
/// the load-mode clamp, say -- fails here instead of reaching the manager.
ConformanceOutcome runLaunchIntentHostile(Map<String, Object?> body) {
  final intent = body['intent']! as Map<String, Object?>;
  final emitted = _writerShapesFor(intent);
  for (final candidate in emitted) {
    if (deepEquals(candidate, intent)) {
      return ConformanceOutcome(
        false,
        const {'writer-can-emit'},
        detail:
            'the launcher writes this intent, so it is not hostile input: '
            '$intent',
      );
    }
  }
  return ConformanceOutcome(false, const {
    'writer-cannot-emit',
  }, detail: 'no selection produced $intent; the writer emitted $emitted');
}

/// Every intent the writer could produce from the fields this one carries, under
/// both settings of the only flag that changes its shape.
List<Map<String, Object?>> _writerShapesFor(Map<String, Object?> intent) {
  final shapes = <Map<String, Object?>>[];
  for (final launchIntoGamemode in const [true, false]) {
    final selection = <String, Object?>{
      if (intent['worldId'] is String) 'worldId': intent['worldId'],
      if (intent['gamemodeId'] is String) 'gamemodeId': intent['gamemodeId'],
      if (intent['loadMode'] is String) 'loadMode': intent['loadMode'],
      'launchIntoGamemode': launchIntoGamemode,
    };
    try {
      shapes.add(WorldSelection.fromJson(selection).toLaunchIntentJson());
    } on FormatException {
      // A selection the launcher would refuse to restore cannot be a selection
      // the launcher then writes an intent from.
      continue;
    }
  }
  return shapes;
}

bool deepEquals(Object? left, Object? right) {
  if (left is Map && right is Map) {
    if (left.length != right.length) {
      return false;
    }
    for (final key in left.keys) {
      if (!right.containsKey(key) || !deepEquals(left[key], right[key])) {
        return false;
      }
    }
    return true;
  }
  if (left is List && right is List) {
    if (left.length != right.length) {
      return false;
    }
    for (var index = 0; index < left.length; index++) {
      if (!deepEquals(left[index], right[index])) {
        return false;
      }
    }
    return true;
  }
  return left == right;
}
