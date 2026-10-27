import 'dart:io';
import 'package:launcher_domain/launcher_domain.dart';
import 'package:path/path.dart' as p;
import 'bounded_file_reader.dart';
import 'map_world_authoring.dart';

/// Enable only the fresh gamemode template's map example and its deterministic test.
Future<void> enableMapGamemodeTemplate(String project) async {
  final root = p.normalize(p.absolute(project));
  var entityId = 'replace-with-surveyed-id';
  final snapshot = File(p.join(root, 'obj', 'topiaforge-map-survey.json'));
  ordinaryMapPath(snapshot.path);
  if (snapshot.existsSync()) {
    final data = readBoundedJsonObjectSync(
      snapshot,
      maxBytes: 64 * 1024 * 1024,
    );
    final records = data['records'];
    if (records is List) {
      final candidates = records
          .whereType<Map>()
          .where(
            (r) =>
                r['id'] is String &&
                MapReference.isId(r['id'] as String, maximum: 64),
          )
          .toList();
      final modeled = candidates.where((r) => r['modelId'] is String).toList();
      if (modeled.isNotEmpty) {
        entityId = modeled.first['id'] as String;
      } else if (candidates.isNotEmpty) {
        entityId = candidates.first['id'] as String;
      }
    }
  }
  // This function runs only immediately after creating the template, before the author edits it.
  for (final file in Directory(
    root,
  ).listSync(recursive: true, followLinks: false).whereType<File>()) {
    if (p.extension(file.path) != '.cs') continue;
    ordinaryMapPath(file.path);
    final text = readBoundedTextFileSync(file, maxBytes: 256 * 1024);
    if (text.contains('TOPIAFORGE_AUTHORED_MAP')) {
      await file.writeAsString(
        text.replaceAll('"replace-with-surveyed-id"', '"$entityId"'),
      );
    }
  }
  final props = File(p.join(root, 'topiaforge.map.props'));
  ordinaryMapPath(props.path);
  await props.create(exclusive: true);
  await props.writeAsString(
    '<Project>\n  <PropertyGroup>\n'
    '    <DefineConstants>\$(DefineConstants);TOPIAFORGE_AUTHORED_MAP</DefineConstants>\n'
    '  </PropertyGroup>\n</Project>\n',
  );
}
