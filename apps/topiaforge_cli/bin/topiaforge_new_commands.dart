part of 'topiaforge.dart';

/// `topiaforge new ...` — project scaffolding (mods and Unity world projects), split out of
/// topiaforge.dart to keep the entry file under the file-size cap.
extension _NewCommands on _TopiaForgeCli {
  Future<int> _new(List<String> args) async {
    if (args.firstOrNull == 'unity-world') {
      if (args.length < 2) {
        throw UsageError(
          'Usage: topiaforge new unity-world <name> [--dir Path]',
        );
      }
      final name = args[1];
      final parent = _option(args, '--dir') ?? Directory.current.path;
      final projects = await developerRepository.createUnityProject(
        parentDirectory: parent,
        name: name,
      );
      stdout.writeln(
        'Created Unity world project "$name" (${projects.length} project(s) tracked). '
        'Open it in Unity and author UGC markers, then Go Live from the launcher cockpit.',
      );
      final pairedMod = _option(args, '--mod');
      if (pairedMod != null) {
        final projectPath = projects
            .firstWhere(
              (project) => p.basename(project.path) == name,
              orElse: () => projects.last,
            )
            .path;
        await _worldLink(['--project', projectPath, '--mod', pairedMod]);
      }
      if (args.contains('--live-sync')) {
        final projectPath = projects
            .firstWhere(
              (project) => p.basename(project.path) == name,
              orElse: () => projects.last,
            )
            .path;
        final watch = await _resolveWatchFolder(
          _option(args, '--watch'),
          fallbackRoot: projectPath,
        );
        final seed = await developerRepository.writeUgcCompanionSeed(
          projectPath,
          watchFolder: watch,
          projectName: name,
          sceneId: _option(args, '--scene') ?? '',
        );
        stdout.writeln(
          'Live sync seeded ($seed, watch folder $watch). Launch it with `topiaforge ugc dev --project "$projectPath"`.',
        );
      }
      return 0;
    }
    if (args.firstOrNull != 'mod' || args.length < 2) {
      throw UsageError(
        'Usage: topiaforge new mod <id> [--template id] [--name Name] [--dir Path] [flags...]\n'
        '       topiaforge new unity-world <name> [--dir Path] [--live-sync [--watch folder]]\n'
        'Run `topiaforge list templates` for template ids and `topiaforge help` for the full flag list.',
      );
    }
    final id = args[1];
    final name = _option(args, '--name') ?? id;
    final parent = _option(args, '--dir') ?? Directory.current.path;
    final options = _parseModScaffoldOptions(args);
    final workspace = await developerRepository.createModProject(
      parentDirectory: parent,
      id: id,
      name: name,
      options: options,
    );
    stdout.writeln('Created ${workspace.projectRoot}');
    stdout.writeln(
      'Next: edit topiaforge.mod.json (or use `topiaforge mod set|add|remove`), then validate with '
      '`topiaforge check package ${workspace.projectRoot}`.',
    );
    if (options.template == 'world') {
      final unityProject = _option(args, '--unity-project');
      if (unityProject != null) {
        await _worldLink([
          '--project',
          unityProject,
          '--mod',
          workspace.projectRoot,
        ]);
      } else {
        stdout.writeln(
          'World mod: pair a Unity authoring project with '
          '`topiaforge new unity-world <Name> --mod ${workspace.projectRoot}` '
          '(or `topiaforge world link`), then build its bundle with `topiaforge world build`.',
        );
      }
    }
    if (options.includeUnityCompanion || options.liveSync != null) {
      stdout.writeln(
        'Unity companion scaffolded in unity-companion/. Open it in Unity and use '
        'TopiaForge → UGC Live Sync to author and live-sync UGC content into the running game.',
      );
    }
    if (options.liveSync != null) {
      stdout.writeln(
        'Live sync preconfigured (${options.liveSync!.transport}). Deploy it to the game with '
        '`topiaforge ugc setup` and start the full loop with `topiaforge ugc dev`.',
      );
    }
    return 0;
  }

  /// Parses every scaffold-time manifest flag of `new mod`. `hashes`
  /// (pack-time) and `schemaVersion` (pinned to 3) are intentionally not flags.
  ModScaffoldOptions _parseModScaffoldOptions(List<String> args) {
    ModDependency parseDependency(String spec, {bool optional = false}) {
      final at = spec.indexOf('@');
      return ModDependency(
        id: at < 0 ? spec : spec.substring(0, at),
        versionRange: at < 0
            ? const VersionRange.any()
            : VersionRange.parse(spec.substring(at + 1)),
        optional: optional,
      );
    }

    ModConflict parseConflict(String spec) {
      final at = spec.indexOf('@');
      return ModConflict(
        id: at < 0 ? spec : spec.substring(0, at),
        versionRange: at < 0
            ? const VersionRange.any()
            : VersionRange.parse(spec.substring(at + 1)),
      );
    }

    GamemodeDefinition parseGamemode(String spec) {
      final parts = spec.split(':');
      if (parts.first.trim().isEmpty) {
        throw StateError('--gamemode expects <id:Name[:description]>.');
      }
      return GamemodeDefinition(
        id: parts[0].trim(),
        name: parts.length > 1 && parts[1].trim().isNotEmpty
            ? parts[1].trim()
            : parts[0].trim(),
        description: parts.length > 2 ? parts.sublist(2).join(':').trim() : '',
      );
    }

    VersionRange? parseRange(String? value) =>
        value == null ? null : VersionRange.parse(value);

    final liveSync = args.contains('--live-sync')
        ? UgcLiveSyncSettings(
            transport: UgcLiveSyncSettings.normalizeTransport(
              _option(args, '--transport'),
            ),
            watchFolder: _option(args, '--watch') ?? '',
            sceneId: _option(args, '--scene') ?? '',
          )
        : null;
    final licenseText = _readLicenseText(args);

    return ModScaffoldOptions(
      template: _option(args, '--template') ?? 'minimal',
      description: _option(args, '--description'),
      license: _option(args, '--license'),
      licenseText: licenseText,
      category: _option(args, '--category'),
      authorName: _option(args, '--author'),
      authorEmail: _option(args, '--author-email'),
      authorUrl: _option(args, '--author-url'),
      tags: _options(args, '--tag'),
      permissions: _options(args, '--permission'),
      screenshots: _options(args, '--screenshot'),
      loadAfter: _options(args, '--load-after'),
      apiAssemblies: _options(args, '--api-assembly'),
      dependencies: _options(
        args,
        '--dependency',
      ).map(parseDependency).toList(),
      optionalDependencies: _options(
        args,
        '--optional-dependency',
      ).map((spec) => parseDependency(spec, optional: true)).toList(),
      conflicts: _options(args, '--conflict').map(parseConflict).toList(),
      gamemodes: _options(args, '--gamemode').map(parseGamemode).toList(),
      entryAssembly: _option(args, '--entry-assembly'),
      entryType: _option(args, '--entry-type'),
      gameVersionRange: parseRange(_option(args, '--game-version-range')),
      loaderVersionRange: parseRange(_option(args, '--loader-version-range')),
      sdkVersionRange: parseRange(_option(args, '--sdk-version-range')),
      icon: _option(args, '--icon'),
      homepage: _option(args, '--homepage'),
      source: _option(args, '--source'),
      includeUnityCompanion: args.contains('--unity-companion'),
      liveSync: liveSync,
    );
  }

  String? _readLicenseText(List<String> args) {
    final rawPath = _option(args, '--license-file');
    if (rawPath == null) return null;
    if (_option(args, '--license') == null) {
      throw UsageError(
        '--license-file requires an explicit --license SPDX expression.',
      );
    }
    final path = p.normalize(p.absolute(rawPath));
    return readBoundedTextFileSync(
      File(path),
      maxBytes: CliFileLimits.manifest,
    );
  }
}
