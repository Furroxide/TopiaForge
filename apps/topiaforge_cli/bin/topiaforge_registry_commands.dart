part of 'topiaforge.dart';

extension _RegistryCommands on _TopiaForgeCli {
  Future<int> _registry(List<String> args) async {
    final command = args.firstOrNull;
    if (command == null || command == 'help' || command == '--help') {
      _printRegistryHelp();
      return 0;
    }
    return switch (command) {
      'index' => _registryIndex(args.skip(1).toList()),
      'add-entry' => _registryAddEntry(args.skip(1).toList()),
      'validate' => _registryValidate(args.skip(1).toList()),
      _ => throw UsageError(
        'Usage: topiaforge registry index|add-entry|validate ...',
      ),
    };
  }

  Future<int> _registryIndex(List<String> args) async {
    final repository = _option(args, '--repository') ?? '';
    final packagesDir = _option(args, '--dir') ?? '';
    final output = _option(args, '--output');
    if (output == null || (repository.isEmpty == packagesDir.isEmpty)) {
      throw UsageError(
        'Usage: topiaforge registry index (--repository owner/name | --dir packages) '
        '--output path [--entries registry] [--changelogs mods] '
        '[--base-url url] [--include-prerelease]',
      );
    }

    final config = ModRegistryIndexConfig(
      repository: repository,
      packagesDirectory: packagesDir,
      outputDirectory: output,
      entriesDirectory: _option(args, '--entries') ?? '',
      changelogsDirectory: _option(args, '--changelogs') ?? '',
      baseUrl: _option(args, '--base-url') ?? '',
      includePrerelease: args.contains('--include-prerelease'),
    );

    HttpGitHubReleaseClient? client;
    try {
      if (repository.isNotEmpty) {
        client = HttpGitHubReleaseClient(
          token: Platform.environment['GITHUB_TOKEN'],
        );
      }
      final builder = ModRegistryIndexBuilder(client: client);
      final result = await builder.build(config);
      stdout.writeln(
        'Wrote ${result.firstPartyCount} first-party and '
        '${result.communityCount} community mod(s).',
      );
      stdout.writeln('Index: ${result.indexPath}');
      return 0;
    } finally {
      client?.close();
    }
  }

  Future<int> _registryAddEntry(List<String> args) async {
    String? packagePath;
    for (var index = 0; index < args.length; index++) {
      if (args[index].startsWith('--')) {
        index++; // Every add-entry flag takes one value.
        continue;
      }
      packagePath = args[index];
      break;
    }
    final url = _option(args, '--url');
    if (packagePath == null || url == null) {
      throw UsageError(
        'Usage: topiaforge registry add-entry <package.topiaforgemod> '
        '--url <https download url> [--changelog text|@file] [--output dir]',
      );
    }
    final packageFile = File(packagePath);
    if (!packageFile.existsSync()) {
      stderr.writeln('Package file does not exist: $packagePath');
      return 1;
    }

    var changelog = _option(args, '--changelog') ?? '';
    if (changelog.startsWith('@')) {
      final changelogFile = File(changelog.substring(1));
      if (!changelogFile.existsSync()) {
        stderr.writeln(
          'Changelog file does not exist: ${changelog.substring(1)}',
        );
        return 1;
      }
      changelog = readBoundedTextFileSync(
        changelogFile,
        maxBytes: CliFileLimits.changelog,
        allowEmpty: true,
      ).trim();
    }

    final outputDir =
        _option(args, '--output') ??
        (() {
          final root = _findRepoRoot();
          return root == null
              ? Directory.current.path
              : p.join(root, 'registry');
        })();

    final package = readModPackage(
      readBoundedRegularFileSync(packageFile, maxBytes: CliFileLimits.package),
    );
    final entryPath = p.join(
      outputDir,
      '${package.manifest.id.toLowerCase()}.json',
    );
    RegistryEntryFile? existing;
    final entryFile = File(entryPath);
    if (entryFile.existsSync()) {
      try {
        existing = RegistryEntryFile.fromJson(
          readBoundedJsonObjectSync(
            entryFile,
            maxBytes: CliFileLimits.registryEntry,
          ),
        );
      } on Object {
        stderr.writeln(
          'Existing entry file is not valid JSON — fix or delete it first: '
          '$entryPath',
        );
        return 1;
      }
    }

    final result = buildRegistryEntry(
      package: package,
      downloadUrl: url,
      changelog: changelog,
      existing: existing,
    );
    _printIssues(result.issues);
    final entry = result.entryFile;
    if (!result.ok || entry == null) {
      return 1;
    }

    await Directory(outputDir).create(recursive: true);
    final json = const JsonEncoder.withIndent('  ').convert(entry.toJson());
    await entryFile.writeAsString('$json\n');

    final sizeMb = (package.byteLength / (1024 * 1024)).toStringAsFixed(1);
    stdout.writeln('Wrote $entryPath');
    stdout.writeln(
      '${package.manifest.id} ${package.manifest.version} '
      'sha256=${package.sha256Hex} ($sizeMb MB)',
    );
    stdout.writeln('');
    stdout.writeln('Next steps:');
    stdout.writeln('  1. Host the exact packed file at: $url');
    stdout.writeln(
      '     (a GitHub Release asset works well; never replace a published file '
      '— the sha256 is pinned)',
    );
    stdout.writeln('  2. Publish this entry from your own static registry.');
    stdout.writeln(
      '  3. Validate and install that self-hosted source before announcing it.',
    );
    stdout.writeln('  Guide: docs/PublishingYourMod.md (self-hosting).');
    return 0;
  }

  Future<int> _registryValidate(List<String> args) async {
    final root = _findRepoRoot();
    final entries =
        _option(args, '--entries') ??
        (root == null ? null : p.join(root, 'registry'));
    if (entries == null) {
      throw UsageError(
        'Usage: topiaforge registry validate [--entries dir] [--mods dir] '
        '[--only file]... [--offline] [--publication --previous-entries dir]',
      );
    }
    final mods =
        _option(args, '--mods') ?? (root == null ? '' : p.join(root, 'mods'));

    final validator = ModRegistryValidator();
    final result = await validator.validate(
      ModRegistryValidationOptions(
        entriesDirectory: entries,
        modsDirectory: mods,
        onlyFiles: _options(args, '--only'),
        download: !args.contains('--offline'),
        publication: args.contains('--publication'),
        previousEntriesDirectory: _option(args, '--previous-entries') ?? '',
      ),
    );

    _printIssues(result.globalIssues);
    for (final report in result.reports) {
      if (report.issues.isEmpty) {
        stdout.writeln('${report.fileName}: OK');
        continue;
      }
      stdout.writeln('${report.fileName}:');
      for (final issue in report.issues) {
        stdout.writeln('  ${issue.severity.name}: ${issue.message}');
      }
    }
    if (result.reports.isEmpty) {
      stdout.writeln('No registry entry files found in $entries.');
    }
    return result.ok ? 0 : 1;
  }

  void _printRegistryHelp() {
    stdout.writeln('Publish mods to a registry players can browse.');
    stdout.writeln('');
    stdout.writeln(
      '  topiaforge registry add-entry <pkg> --url <https url> [--changelog text|@file]',
    );
    stdout.writeln(
      '      Create or update an entry for a self-hosted registry.',
    );
    stdout.writeln(
      '  topiaforge registry index (--repository owner/name | --dir packages) --output path',
    );
    stdout.writeln(
      '      Build an index.json (options: --entries, --changelogs, --base-url,',
    );
    stdout.writeln('      --include-prerelease).');
    stdout.writeln(
      '  topiaforge registry validate [--only file] [--offline] [--publication --previous-entries dir]',
    );
    stdout.writeln(
      '      Check self-hosted entries; publication mode enforces closed official intake and append-only history.',
    );
    stdout.writeln('');
    stdout.writeln(
      'Docs: docs/PublishingYourMod.md and docs/RegistryFormat.md.',
    );
  }
}
