import 'sandbox_game_build.dart';
import 'sandbox_json.dart';

/// The reviewed expected-inventory baseline lives beside the driver manifest.
/// Its `gameBuild` must equal the pinned build: an inventory reviewed for
/// another build is refused, so a retarget cannot carry a stale review.
const expectedCatalogFileName = 'expected-catalog-v1.json';

/// Reviewed expected per-type catalog inventory (spec section 5).
///
/// While [reviewed] is false the catalog-editing scenario cannot pass; the
/// verifier reports `unavailable`. A listed source or entry that is absent from
/// the observed catalog is a hard failure regardless of the reviewed flag. The
/// bytes are supplied by a checked-in maintainer baseline resolved beside the
/// driver manifest; parsing never establishes a passing case.
final class SandboxExpectedCatalog {
  SandboxExpectedCatalog._(this.reviewed, this.sources, this.entries);

  final bool reviewed;
  final List<SandboxExpectedSource> sources;
  final List<SandboxExpectedEntry> entries;

  static const reviewedGateReason = 'expected inventory baseline not reviewed';

  factory SandboxExpectedCatalog.parse(List<int> bytes) {
    final json = sandboxDocument(bytes, 'expected catalog inventory');
    sandboxFields(json, {
      'schemaVersion',
      'kind',
      'gameBuild',
      'reviewed',
      'sources',
      'entries',
    }, 'expected catalog inventory');
    if (json['schemaVersion'] is! int ||
        json['schemaVersion'] != 1 ||
        json['kind'] != 'sandbox-native-expected-catalog-v1' ||
        json['gameBuild'] is! int ||
        json['gameBuild'] != sandboxGameBuild ||
        json['reviewed'] is! bool) {
      throw StateError('Unsupported expected catalog identity.');
    }
    final sources = _rows(
      json['sources'],
    ).map(SandboxExpectedSource._).toList();
    final entries = _rows(json['entries']).map(SandboxExpectedEntry._).toList();
    if (sources.map((s) => s.id).toSet().length != sources.length) {
      throw StateError('Expected catalog repeats a source id.');
    }
    if (entries.map((e) => e.rowId).toSet().length != entries.length) {
      throw StateError('Expected catalog repeats a row id.');
    }
    return SandboxExpectedCatalog._(
      json['reviewed']! as bool,
      List.unmodifiable(sources),
      List.unmodifiable(entries),
    );
  }

  /// Fails when any listed expectation is missing from the observed catalog.
  /// [observedEntries] are the union of `catalog` and `robotCatalog` rows;
  /// [observedSources] are the `catalogSources` rows.
  void requirePresent(
    List<Map<String, Object?>> observedEntries,
    List<Map<String, Object?>> observedSources,
  ) {
    for (final source in sources) {
      final match = observedSources.where((s) => s['id'] == source.id).toList();
      if (match.length != 1) {
        throw StateError(
          'Expected catalog source ${source.id} is absent from the observed catalog.',
        );
      }
      final observed = match.single;
      final count = observed['entryCount'];
      if (observed['state'] != source.expectedState ||
          count is! int ||
          count < source.minimumEntries) {
        throw StateError(
          'Expected catalog source ${source.id} state or entry count is missing.',
        );
      }
    }
    for (final entry in entries) {
      final match = observedEntries
          .where((e) => e['rowId'] == entry.rowId)
          .toList();
      if (match.length != 1) {
        throw StateError(
          'Expected catalog entry ${entry.rowId} is absent from the observed catalog.',
        );
      }
      final observed = match.single;
      if (observed['kind'] != entry.kind ||
          observed['transformCapabilities'] != entry.transformCapabilities) {
        throw StateError(
          'Expected catalog entry ${entry.rowId} kind or capabilities differ.',
        );
      }
    }
  }

  static List<Map<String, Object?>> _rows(Object? value) {
    if (value is! List<Object?> || value.length > 512) {
      throw StateError('Expected catalog list is invalid.');
    }
    return value.map((v) => sandboxObject(v, 'expected catalog row')).toList();
  }
}

final class SandboxExpectedSource {
  SandboxExpectedSource._(Map<String, Object?> json)
    : id = sandboxText(json['id'], 'expected source id', maximum: 256),
      expectedState = sandboxText(
        json['expectedState'],
        'expected source state',
        maximum: 64,
      ),
      minimumEntries = _count(json['minimumEntries']) {
    sandboxFields(json, {
      'id',
      'expectedState',
      'minimumEntries',
    }, 'expected catalog source');
  }
  final String id, expectedState;
  final int minimumEntries;
}

final class SandboxExpectedEntry {
  SandboxExpectedEntry._(Map<String, Object?> json)
    : rowId = sandboxText(json['rowId'], 'expected row id', maximum: 256),
      kind = sandboxText(json['kind'], 'expected row kind', maximum: 64),
      transformCapabilities = _capabilities(json['transformCapabilities']) {
    sandboxFields(json, {
      'rowId',
      'kind',
      'transformCapabilities',
    }, 'expected catalog entry');
  }
  final String rowId, kind;
  final int transformCapabilities;
}

int _count(Object? value) {
  if (value is! int || value < 0 || value > 1000000) {
    throw StateError('Expected catalog minimum entry count is invalid.');
  }
  return value;
}

int _capabilities(Object? value) {
  if (value is! int || value < 0 || value > 7) {
    throw StateError('Expected catalog transform capabilities are invalid.');
  }
  return value;
}
