import 'dart:convert';

import 'json_value_preservation.dart';
import 'models.dart';
import 'versioning.dart';

part 'manifest_migration_normalization.dart';
part 'manifest_migration_guards.dart';

enum ManifestMigrationMode { automatic, stub }

enum ManifestMigrationDisposition { unchanged, migrated, invalidStub, refused }

final class ManifestMigrationDiagnostic {
  ManifestMigrationDiagnostic({
    required this.sourceLabel,
    required this.code,
    required this.jsonPointer,
    required this.message,
    this.originalIndex,
    this.entryId,
    List<String> requiredAuthorFields = const [],
  }) : requiredAuthorFields = List.unmodifiable(requiredAuthorFields);
  final String sourceLabel, code, jsonPointer, message;
  final int? originalIndex;
  final String? entryId;
  final List<String> requiredAuthorFields;
  @override
  String toString() =>
      '$sourceLabel$jsonPointer: $message'
      '${entryId == null ? '' : ' (ID: $entryId)'}';
}

final class ManifestMigrationPlan {
  ManifestMigrationPlan._({
    required this.sourceText,
    required this.sourceLabel,
    required this.mode,
    required this.disposition,
    this.sourceVersion,
    Map<String, Object?>? outputJson,
    List<ManifestMigrationDiagnostic> diagnostics = const [],
  }) : outputJson = outputJson == null
           ? null
           : _freeze(outputJson) as Map<String, Object?>,
       diagnostics = List.unmodifiable(diagnostics);
  final String sourceText, sourceLabel;
  final int? sourceVersion;
  final ManifestMigrationMode mode;
  final ManifestMigrationDisposition disposition;
  final Map<String, Object?>? outputJson;
  final List<ManifestMigrationDiagnostic> diagnostics;
  String? get outputText => outputJson == null
      ? null
      : '${const JsonEncoder.withIndent('  ').convert(outputJson)}\n';
}

Object? _freeze(Object? value) => switch (value) {
  Map map => Map<String, Object?>.unmodifiable({
    for (final entry in map.entries) entry.key as String: _freeze(entry.value),
  }),
  List list => List<Object?>.unmodifiable(list.map(_freeze)),
  _ => value,
};

final class ManifestMigrationPlanner {
  const ManifestMigrationPlanner();
  ManifestMigrationPlan plan(
    String sourceText, {
    required String sourceLabel,
    ManifestMigrationMode mode = ManifestMigrationMode.automatic,
  }) {
    final work = _MigrationWork(sourceText, sourceLabel, mode);
    return work.run();
  }
}

final class _MigrationWork {
  _MigrationWork(this.text, this.label, this.mode);
  final String text, label;
  final ManifestMigrationMode mode;
  final diagnostics = <ManifestMigrationDiagnostic>[];
  bool malformed = false;
  int? version;
  void issue(
    String code,
    String path,
    String message, {
    int? index,
    String? id,
    List<String> requiredFields = const [],
    bool fatal = true,
  }) {
    malformed |= fatal;
    diagnostics.add(
      ManifestMigrationDiagnostic(
        sourceLabel: label,
        code: code,
        jsonPointer: path,
        message: message,
        originalIndex: index,
        entryId: id,
        requiredAuthorFields: requiredFields,
      ),
    );
  }

  ManifestMigrationPlan result(
    ManifestMigrationDisposition disposition, [
    Map<String, Object?>? output,
  ]) => ManifestMigrationPlan._(
    sourceText: text,
    sourceLabel: label,
    mode: mode,
    disposition: disposition,
    sourceVersion: version,
    outputJson: output,
    diagnostics: diagnostics,
  );
  ManifestMigrationPlan run() {
    Map<String, Object?> map;
    try {
      if (utf8.encode(text).length > 1024 * 1024) {
        throw const FormatException('Manifest exceeds the 1 MiB input limit.');
      }
      final raw = decodeJsonPreservingValues(text, label: label);
      if (raw is! Map<String, Object?>) {
        throw const FormatException('Manifest root must be an object.');
      }
      map = raw;
      final rawVersion = map['schemaVersion'];
      if (rawVersion is! int || ![3, 4, 5, 6].contains(rawVersion)) {
        issue(
          'schema-version',
          '/schemaVersion',
          'Expected integer schemaVersion 3, 4, 5 or 6.',
        );
        return result(ManifestMigrationDisposition.refused);
      }
      version = rawVersion;
    } on FormatException catch (error) {
      issue('invalid-json', '', error.message);
      return result(ManifestMigrationDisposition.refused);
    }
    if (version == 6) {
      _validateCommon(map);
      return result(
        malformed
            ? ManifestMigrationDisposition.refused
            : ManifestMigrationDisposition.unchanged,
      );
    }
    if (map.containsKey('contributions')) {
      issue(
        'legacy-contributions',
        '/contributions',
        'Legacy manifests cannot already contain V6 contributions; repair the source explicitly.',
      );
    }
    final gamemodes = _legacyModes(map);
    _validateMigrationCollections(map);
    if (version == 3) _normalizeV3(map);
    map.remove('worldGamemodes');
    map['schemaVersion'] = 6;
    map[r'$schema'] = ModManifest.canonicalSchemaUrl;
    for (final field in _compatibilityRanges) {
      if (!map.containsKey(field)) {
        issue(
          'missing-required',
          '/$field',
          'Supply the actual required $field; migration cannot infer a compatibility range.',
          requiredFields: [field],
          fatal: false,
        );
      }
    }
    // Validate common fields separately from deliberate, incomplete declarations.
    // Missing ranges stay missing in output; the validation-only copy excludes
    // their already-reported missing-field errors without inventing output values.
    _validateCommon(map, allowMissingRanges: true);
    if (malformed) return result(ManifestMigrationDisposition.refused);
    if (gamemodes.isNotEmpty) {
      final dependencies = <String>[
        for (final field in ['dependencies', 'optionalDependencies'])
          if (map[field] is Map) ...(map[field] as Map).keys.cast<String>(),
      ];
      final owner = map['name'] as String;
      for (var index = 0; index < gamemodes.length; index++) {
        final id = gamemodes[index]['id'] as String;
        final lowered = id.toLowerCase();
        final prefix = '${owner.toLowerCase()}.';
        if (!lowered.startsWith(prefix) ||
            lowered.length <= prefix.length ||
            dependencies.any(
              (dependency) =>
                  dependency.length > owner.length &&
                  (lowered == dependency.toLowerCase() ||
                      lowered.startsWith('${dependency.toLowerCase()}.')),
            )) {
          issue(
            'ownership',
            '/worldGamemodes/$index/id',
            'The declaration must belong to $owner without entering a dependency namespace; retain the ID until the author repairs it.',
            index: index,
            id: id,
            fatal: false,
            requiredFields: ['id'],
          );
        }
        issue(
          'missing-implementation',
          '/worldGamemodes/$index',
          'Provide the gamemode implementation binding. Worlds, launch targets, spawn settings and optional worldRequirements are separate author decisions.',
          index: index,
          id: id,
          fatal: false,
          requiredFields: ['implementation'],
        );
      }
      if (gamemodes.length > 16) {
        issue(
          'contribution-limit',
          '/worldGamemodes',
          'V6 permits at most 16 gamemodes; the author must reorganize these declarations. All original entries are retained.',
          fatal: false,
        );
      }
      map['contributions'] = {'gamemodes': gamemodes};
      final caps = map['capabilities'] as List? ?? <Object?>[];
      if (!caps.contains('world-service')) {
        map['capabilities'] = [...caps, 'world-service'];
        issue(
          'required-capability',
          '/capabilities',
          'V6 gamemode declarations require world-service; the requested stub adds this capability while preserving existing capabilities.',
          fatal: false,
        );
      }
    }
    if (diagnostics.isNotEmpty) {
      if (mode != ManifestMigrationMode.stub) {
        return result(ManifestMigrationDisposition.refused);
      }
      if (map.containsKey('x-migration-todo')) {
        issue(
          'reserved-extension',
          '/x-migration-todo',
          'Existing migration notes cannot be overwritten; preserve or rename them explicitly.',
        );
        return result(ManifestMigrationDisposition.refused);
      }
      map['x-migration-todo'] = [
        for (final diagnostic in diagnostics)
          {
            'code': diagnostic.code,
            'sourcePath': diagnostic.jsonPointer,
            if (diagnostic.originalIndex != null)
              'originalIndex': diagnostic.originalIndex,
            if (diagnostic.entryId != null) 'id': diagnostic.entryId,
            'requiredFields': diagnostic.requiredAuthorFields,
            'message': diagnostic.message,
          },
      ];
      return result(ManifestMigrationDisposition.invalidStub, map);
    }
    return result(ManifestMigrationDisposition.migrated, map);
  }
}
