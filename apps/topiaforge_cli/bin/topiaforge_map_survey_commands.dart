part of 'topiaforge.dart';

extension _MapSurveyCommands on _TopiaForgeCli {
  Future<int> _worldSurvey(List<String> args) async {
    if (args.isEmpty || args.first.startsWith('--')) {
      throw UsageError(
        'Usage: topiaforge world survey <world> [--project dir] '
        '[--anchors map-anchors.json] [--update] [--offline]',
      );
    }
    final result = await surveyMapWorld(
      _option(args, '--project') ?? Directory.current.path,
      args.first,
      anchorsPath: _option(args, '--anchors') ?? 'map-anchors.json',
      update: args.contains('--update'),
      offline: args.contains('--offline'),
    );
    stdout.writeln(
      'Survey saved under obj/: ${result.entityCount} authored entities.',
    );
    stdout.writeln(result.report);
    if (result.updated) {
      stdout.writeln(
        'Matched anchors updated. Include the anchors file in the mod package.',
      );
    }
    return result.complete ? 0 : 1;
  }
}
