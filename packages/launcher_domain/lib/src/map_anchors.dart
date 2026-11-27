import 'dart:convert';
import 'authored_map.dart';
import 'strict_map_json.dart';

class SurveyMapAnchor {
  SurveyMapAnchor(
    this.key,
    this.entityId, {
    this.name,
    this.modelId,
    this.editorPosition,
    this.required = true,
    this.positionTolerance = 1,
  }) {
    if (!MapEditorVector.isId(key) ||
        !MapEditorVector.isId(entityId) ||
        !positionTolerance.isFinite ||
        positionTolerance < 0 ||
        positionTolerance > 100) {
      throw const FormatException(
        'Invalid anchor identity or positionTolerance.',
      );
    }
  }
  final String key, entityId;
  final String? name, modelId;
  final MapEditorVector? editorPosition;
  final bool required;
  final double positionTolerance;
  Map<String, Object?> toJson() => {
    'key': key,
    'entityId': entityId,
    if (name != null) 'name': name,
    if (modelId != null) 'modelId': modelId,
    if (editorPosition != null) 'editorPosition': editorPosition!.toJson(),
    'required': required,
    'positionTolerance': positionTolerance,
  };
  SurveyMapAnchor updated(MapAuthoredEntity entity) => SurveyMapAnchor(
    key,
    entity.id,
    name: entity.name,
    modelId: entity.modelId,
    editorPosition: entity.transform.position,
    required: required,
    positionTolerance: positionTolerance,
  );
  static List<SurveyMapAnchor> read(String text) {
    if (utf8.encode(text).length > 256 * 1024) {
      throw const FormatException('Anchors exceed 256 KiB.');
    }
    final data = MapEditorVector.object(decodeStrictMapJson(text));
    final entries = data['anchors'];
    if (data['schemaVersion'] != 1 ||
        entries is! List ||
        entries.length > 1024) {
      throw const FormatException('Invalid anchors schemaVersion or entries.');
    }
    final keys = <String>{};
    return List.unmodifiable(
      entries.map((raw) {
        final item = MapEditorVector.object(raw);
        final required = item['required'];
        if (required != null && required is! bool) {
          throw const FormatException('Invalid anchor required field.');
        }
        final key = MapEditorVector.text(item, 'key', 64);
        if (!keys.add(key)) {
          throw const FormatException('Duplicate anchor key.');
        }
        final name = MapEditorVector.text(item, 'name', 128),
            model = MapEditorVector.text(item, 'modelId', 256);
        return SurveyMapAnchor(
          key,
          MapEditorVector.text(item, 'entityId', 64),
          name: name.isEmpty ? null : name,
          modelId: model.isEmpty ? null : model,
          editorPosition: item['editorPosition'] == null
              ? null
              : MapEditorVector.read(item['editorPosition']),
          required: required as bool? ?? true,
          positionTolerance: MapEditorVector.number(
            item['positionTolerance'] ?? 1,
          ),
        );
      }),
    );
  }
}

enum SurveyAnchorMatch { id, nameModelAndPosition, ambiguous, missing }

class SurveyResolvedAnchor {
  SurveyResolvedAnchor(this.anchor, this.match, this.entity, this.changed);
  final SurveyMapAnchor anchor;
  final SurveyAnchorMatch match;
  final MapAuthoredEntity? entity;
  final bool changed;
  String get matchName => switch (match) {
    SurveyAnchorMatch.id => 'Id',
    SurveyAnchorMatch.nameModelAndPosition => 'NameModelAndPosition',
    SurveyAnchorMatch.ambiguous => 'Ambiguous',
    SurveyAnchorMatch.missing => 'Missing',
  };
}

class SurveyAnchorResolution {
  SurveyAnchorResolution._(this.anchors);
  final List<SurveyResolvedAnchor> anchors;
  bool get isComplete =>
      anchors.every((a) => !a.anchor.required || a.entity != null);
  String get report =>
      '${anchors.map((a) => '${a.anchor.key}: ${a.matchName}${a.changed ? ' (changed since survey)' : ''}.').join('\n')}\n'
      'Run topiaforge world survey <world> to re-survey.';
  Map<String, dynamic> updatedFile() => {
    'schemaVersion': 1,
    'anchors': anchors
        .map(
          (a) => (a.entity == null ? a.anchor : a.anchor.updated(a.entity!))
              .toJson(),
        )
        .toList(),
  };
  factory SurveyAnchorResolution.resolve(
    MapAuthoredScene scene,
    List<SurveyMapAnchor> anchors,
  ) {
    if (anchors.length > 1024 ||
        anchors.map((a) => a.key).toSet().length != anchors.length) {
      throw const FormatException('Use at most 1024 unique anchor keys.');
    }
    final byId = {for (final entity in scene.entities) entity.id: entity};
    final byModel = <String, List<MapAuthoredEntity>>{};
    for (final entity in scene.entities) {
      if (entity.modelId != null) (byModel[entity.modelId!] ??= []).add(entity);
    }
    return SurveyAnchorResolution._(
      List.unmodifiable(
        anchors.map((anchor) {
          var found = byId[anchor.entityId];
          var match = found != null
              ? SurveyAnchorMatch.id
              : SurveyAnchorMatch.missing;
          if (found == null &&
              anchor.name != null &&
              anchor.modelId != null &&
              anchor.editorPosition != null) {
            final matches =
                (byModel[anchor.modelId] ?? const <MapAuthoredEntity>[])
                    .where(
                      (e) =>
                          e.name == anchor.name &&
                          e.transform.position.distance(
                                anchor.editorPosition!,
                              ) <=
                              anchor.positionTolerance,
                    )
                    .take(2)
                    .toList();
            if (matches.length == 1) {
              found = matches.single;
              match = SurveyAnchorMatch.nameModelAndPosition;
            } else if (matches.length > 1) {
              match = SurveyAnchorMatch.ambiguous;
            }
          }
          final changed =
              found != null &&
              (found.id != anchor.entityId ||
                  anchor.name != null && found.name != anchor.name ||
                  anchor.modelId != null && found.modelId != anchor.modelId ||
                  anchor.editorPosition != null &&
                      found.transform.position.distance(
                            anchor.editorPosition!,
                          ) >
                          0.001);
          return SurveyResolvedAnchor(anchor, match, found, changed);
        }),
      ),
    );
  }
}
