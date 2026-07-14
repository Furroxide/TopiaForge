part of 'topiaforge.dart';

extension _TopiaForgeUpdateCommands on _TopiaForgeCli {
  Future<int> _updates(List<String> args) async {
    final command = args.firstOrNull;
    if (command == null || command == 'help' || command == '--help') {
      _printUpdatesHelp();
      return 0;
    }
    if (command != 'index') {
      throw UsageError(
        'Usage: topiaforge updates index --repository owner/name --output path',
      );
    }
    return _updatesIndex(args.skip(1).toList());
  }

  Future<int> _updatesIndex(List<String> args) async {
    if (args.contains('--help') || args.firstOrNull == 'help') {
      _printUpdatesIndexHelp();
      return 0;
    }

    final repository = _option(args, '--repository');
    final output = _option(args, '--output');
    if (repository == null || output == null) {
      throw UsageError(
        'Usage: topiaforge updates index --repository owner/name --output path',
      );
    }

    final client = HttpGitHubReleaseClient(
      token: Platform.environment['GITHUB_TOKEN'],
    );
    try {
      final builder = LauncherUpdateIndexBuilder(client: client);
      final result = await builder.build(
        LauncherUpdateIndexConfig(
          repository: repository,
          outputDirectory: output,
          baseUrl:
              _nonBlank(_option(args, '--base-url')) ??
              _nonBlank(Platform.environment['LAUNCHER_UPDATE_BASE_URL']),
        ),
      );
      stdout.writeln('Wrote ${result.itemCount} launcher update entries.');
      stdout.writeln('Manual releases: ${result.manualReleasesUrl}');
      return 0;
    } finally {
      client.close();
    }
  }

  void _printUpdatesHelp() {
    stdout.writeln(
      'Usage: topiaforge updates index --repository owner/name --output path',
    );
  }

  void _printUpdatesIndexHelp() {
    stdout.writeln(
      'Usage: topiaforge updates index --repository owner/name --output path [--base-url url]',
    );
    stdout.writeln('');
    stdout.writeln('Builds the launcher update JSON index for GitHub Pages.');
    stdout.writeln('Reads GITHUB_TOKEN for GitHub API calls.');
    stdout.writeln(
      'Uses --base-url, LAUNCHER_UPDATE_BASE_URL, or the repository Pages URL.',
    );
  }

  String? _nonBlank(String? value) {
    if (value == null || value.trim().isEmpty) {
      return null;
    }
    return value.trim();
  }
}
