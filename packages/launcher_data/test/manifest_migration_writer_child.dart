import 'dart:convert';
import 'dart:io';
import 'package:launcher_data/launcher_data.dart';
import 'package:launcher_domain/launcher_domain.dart';

Future<void> main(List<String> arguments) async {
  final writer = ManifestMigrationWriter(
    replaceFile: (staging, target) async {
      stdout.writeln('ready');
      await stdout.flush();
      await stdin.transform(utf8.decoder).transform(const LineSplitter()).first;
      staging.renameSync(target.path);
    },
  );
  final snapshot = await writer.prepare(arguments.single);
  final plan = const ManifestMigrationPlanner().plan(
    snapshot.sourceText,
    sourceLabel: snapshot.path,
  );
  await writer.commit(snapshot, plan);
}
