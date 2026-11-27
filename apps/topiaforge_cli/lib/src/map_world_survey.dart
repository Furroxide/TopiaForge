import 'dart:convert';
import 'dart:io';
import 'package:crypto/crypto.dart';
import 'package:launcher_data/launcher_data.dart';
import 'package:launcher_domain/launcher_domain.dart';
import 'package:path/path.dart' as p;
import 'bounded_file_reader.dart';
import 'map_world_authoring.dart';

class WorldSurveyResult {
  WorldSurveyResult(this.entityCount, this.report, this.complete, this.updated);
  final int entityCount;
  final String report;
  final bool complete, updated;
}

/// Refresh one declared map world without running the game or changing its manifest.
Future<WorldSurveyResult> surveyMapWorld(
  String project,
  String worldId, {
  String anchorsPath = 'map-anchors.json',
  bool update = false,
  bool offline = false,
  Future<MapProjectSurvey> Function(MapReference)? readPublished,
}) async {
  final root = p.normalize(p.absolute(project));
  final manifestFile = File(p.join(root, 'topiaforge.mod.json'));
  ordinaryMapPath(manifestFile.path);
  final raw = readBoundedJsonObjectSync(
    manifestFile,
    maxBytes: CliFileLimits.manifest,
  );
  final manifest = ModManifest.fromJson(raw);
  if (manifest.validate().any((i) => i.isBlocking)) {
    throw StateError('Repair the manifest before surveying its world.');
  }
  final worlds =
      manifest.contributions?.worlds ?? const <ModWorldDeclaration>[];
  final matches = worlds.where((w) => w.id == worldId).toList();
  if (matches.length != 1 ||
      matches.single.content?.kind != ModWorldContent.mapKind) {
    throw StateError('Choose a declared map world from this project.');
  }
  final content = matches.single.content!;
  final identity = content.map.isNotEmpty
      ? content.map
      : 'file:${content.file}';
  final key = sha256.convert(utf8.encode(worldId)).toString().substring(0, 16);
  final snapshotFile = File(
    p.join(root, 'obj', 'topiaforge-map-survey-$key.json'),
  );
  ordinaryMapPath(snapshotFile.path);
  final anchorFile = File(_child(root, anchorsPath));
  ordinaryMapPath(anchorFile.path);
  final originalAnchors = anchorFile.existsSync()
      ? readBoundedTextFileSync(anchorFile, maxBytes: 256 * 1024)
      : null;
  final anchors = originalAnchors == null
      ? <SurveyMapAnchor>[]
      : SurveyMapAnchor.read(originalAnchors);
  Map<String, Object?> snapshot;
  MapWorldScene scene;
  if (offline && content.map.isNotEmpty) {
    if (!snapshotFile.existsSync()) {
      throw StateError(
        'No survey snapshot exists for this world. Survey online once, or use a packaged map.',
      );
    }
    final previous = readBoundedJsonObjectSync(
      snapshotFile,
      maxBytes: 64 * 1024 * 1024,
    );
    if (previous['schemaVersion'] != 1 ||
        previous['map'] != identity ||
        previous['world'] != worldId ||
        content.scene.isNotEmpty && previous['scene'] != content.scene ||
        previous['scene'] is! String) {
      throw StateError(
        'The saved survey belongs to different map content. Survey online again.',
      );
    }
    final sceneId = previous['scene'] as String;
    final document = MapWorldDocument.read(
      utf8.encode(
        jsonEncode({
          'name': previous['name'],
          'modified': previous['modified'],
          'scenes': {
            sceneId: {
              'id': sceneId,
              'name': previous['sceneName'],
              'environment': previous['environment'],
              'entities': previous['entities'],
            },
          },
        }),
      ),
    );
    scene = document.select(content.scene.isEmpty ? null : content.scene);
    snapshot = previous;
  } else {
    final MapProjectSurvey survey;
    if (content.file.isNotEmpty) {
      final file = File(_child(root, content.file));
      ordinaryMapPath(file.path);
      final bytes = readBoundedRegularFileSync(
        file,
        maxBytes: MapWorldDocument.maximumPackedBytes,
      );
      survey = MapProjectSurvey.read(bytes);
    } else {
      survey = await (readPublished ?? MapProjectReader().read)(
        MapReference.parse(content.map),
      );
    }
    scene = survey.document.select(
      content.scene.isEmpty ? null : content.scene,
    );
    snapshot = survey.authoredSnapshot(identity, scene, world: worldId);
    snapshot['surveyedUtc'] = DateTime.now().toUtc().toIso8601String();
  }
  final resolution = SurveyAnchorResolution.resolve(scene.authored, anchors);
  if (update && !resolution.isComplete) {
    throw StateError(
      'Required anchors are missing or ambiguous; none were rewritten.\n${resolution.report}',
    );
  }
  Map<String, dynamic>? updatedAnchors;
  if (update) {
    if (originalAnchors == null) {
      throw StateError(
        'Create a map-anchors.json file with the anchors to track first.',
      );
    }
    updatedAnchors = resolution.updatedFile();
    if (_encodedJson(updatedAnchors).length > 256 * 1024) {
      throw StateError('The updated anchors exceed the 256 KiB limit.');
    }
  }
  ordinaryMapPath(snapshotFile.path);
  await snapshotFile.parent.create(recursive: true);
  await _snapshotWrite(snapshotFile, snapshot);
  if (update) {
    await writeMapManifest(anchorFile, originalAnchors!, updatedAnchors!);
  }
  return WorldSurveyResult(
    scene.entities.length,
    '${offline && content.map.isNotEmpty ? 'Using the saved survey; the published revision was not refreshed.\n' : ''}'
    '${originalAnchors == null ? 'No anchors file yet. Add map-anchors.json to track authored entities.\n' : ''}${resolution.report}',
    resolution.isComplete,
    update,
  );
}

String _child(String root, String relative) {
  if (relative.isEmpty ||
      relative.length > 240 ||
      relative.contains(RegExp(r'[\\:*?"<>|\x00-\x1f]')) ||
      relative.split('/').any((s) => s.isEmpty || s == '.' || s == '..')) {
    throw StateError('Use an ordinary package-relative map or anchors path.');
  }
  final path = p.normalize(p.join(root, relative));
  if (!p.isWithin(root, path)) throw StateError('Use a package-relative path.');
  return path;
}

Future<void> _snapshotWrite(File file, Map<String, Object?> value) async {
  final bytes = _encodedJson(value);
  if (bytes.length > 64 * 1024 * 1024) {
    throw StateError('The survey snapshot exceeds the 64 MiB limit.');
  }
  final stage = File(
    '${file.path}.survey-$pid-${DateTime.now().microsecondsSinceEpoch}.tmp',
  );
  try {
    await stage.create(exclusive: true);
    await stage.writeAsBytes(bytes, flush: true);
    ordinaryMapPath(file.path);
    await stage.rename(file.path);
  } finally {
    if (await stage.exists()) await stage.delete();
  }
}

List<int> _encodedJson(Map<String, Object?> value) =>
    utf8.encode('${const JsonEncoder.withIndent('  ').convert(value)}\n');
