import 'dart:io';

import 'package:launcher_data/launcher_data.dart';
import 'package:launcher_domain/launcher_domain.dart';

part 'robotopia_environment_commands.dart';
part 'robotopia_ugc_unity_commands.dart';

Future<void> main(List<String> args) async {
  final cli = _RobotopiaCli(LocalDeveloperRepository());
  try {
    final code = await cli.run(args);
    exitCode = code;
  } on Object catch (error) {
    stderr.writeln(error);
    exitCode = 1;
  }
}

class _RobotopiaCli {
  _RobotopiaCli(this.developerRepository);

  final LocalDeveloperRepository developerRepository;

  Future<int> run(List<String> args) async {
    if (args.isEmpty || args.first == 'help' || args.first == '--help') {
      _printHelp();
      return 0;
    }
    final command = args.first;
    final rest = args.skip(1).toList();
    return switch (command) {
      'new' => _new(rest),
      'check' => _check(rest),
      'add' => _add(rest),
      'remove' => _remove(rest),
      'list' => _list(rest),
      'resolve' => _resolve(rest, restore: false),
      'restore' => _resolve(rest, restore: true),
      'pack' => _pack(rest),
      'install' => _install(rest),
      'launch' => _launch(rest, restart: false),
      'restart' => _launch(rest, restart: true),
      'doctor' => _doctor(rest),
      'compat' => _compat(rest),
      'setup' => _setup(rest),
      'migrate' => _migrate(rest),
      'ugc' => _ugc(rest),
      'projects' => _projects(rest),
      'unity' => _unity(rest),
      _ => _unknown(command),
    };
  }

  Future<int> _new(List<String> args) async {
    if (args.firstOrNull == 'unity-world') {
      if (args.length < 2) {
        throw StateError(
          'Usage: robotopia new unity-world <name> [--dir Path]',
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
      return 0;
    }
    if (args.firstOrNull != 'mod' || args.length < 2) {
      throw StateError(
        'Usage: robotopia new mod <id> [--name Name] [--dir Path] [--unity-companion]\n'
        '       robotopia new unity-world <name> [--dir Path]',
      );
    }
    final id = args[1];
    final name = _option(args, '--name') ?? id;
    final parent = _option(args, '--dir') ?? Directory.current.path;
    final workspace = await developerRepository.createModProject(
      parentDirectory: parent,
      id: id,
      name: name,
      includeUnityCompanion: args.contains('--unity-companion'),
    );
    stdout.writeln('Created ${workspace.projectRoot}');
    if (args.contains('--unity-companion')) {
      stdout.writeln(
        'Unity companion scaffolded in unity-companion/. Open it in Unity and use '
        'Robotopia → UGC Live Sync to author and live-sync UGC content into the running game.',
      );
    }
    return 0;
  }

  Future<int> _check(List<String> args) async {
    return switch (args.firstOrNull) {
      'project' => _checkProject(
        _option(args, '--project') ?? args.skip(1).firstOrNull,
      ),
      'package' => _checkPackage(args.skip(1).firstOrNull),
      _ => throw StateError('Usage: robotopia check project|package [path]'),
    };
  }

  Future<int> _checkProject(String? path) async {
    final workspace = await developerRepository.loadDeveloperWorkspace(
      projectPath: path,
    );
    if (!workspace.hasProject) {
      stdout.writeln('No Robotopia developer project found.');
      return 1;
    }
    stdout.writeln('${workspace.project!.name} (${workspace.project!.id})');
    stdout.writeln('Dependencies: ${workspace.project!.dependencies.length}');
    stdout.writeln('Lock: ${workspace.lock == null ? 'missing' : 'present'}');
    _printIssues(workspace.issues);
    return workspace.hasBlockingIssues ? 1 : 0;
  }

  Future<int> _checkPackage(String? path) async {
    if (path == null) {
      throw StateError('Usage: robotopia check package <path>');
    }
    final manifest = await developerRepository.checkPackage(path);
    stdout.writeln('${manifest.name} ${manifest.version} (${manifest.id})');
    _printIssues(manifest.validate());
    return manifest.validate().any((issue) => issue.isBlocking) ? 1 : 0;
  }

  Future<int> _add(List<String> args) async {
    return switch (args.firstOrNull) {
      'source' => _addSource(args.skip(1).toList()),
      'package' => _addPackage(args.skip(1).toList()),
      _ => throw StateError('Usage: robotopia add source|package ...'),
    };
  }

  Future<int> _addSource(List<String> args) async {
    if (args.length < 2) {
      throw StateError(
        'Usage: robotopia add source <name> <url> [--id id] [--project path]',
      );
    }
    final source = PackageSource(
      id:
          _option(args, '--id') ??
          'source-${DateTime.now().millisecondsSinceEpoch}',
      name: args[0],
      url: args[1],
    );
    final project = await developerRepository.addProjectPackageSource(
      _option(args, '--project') ?? Directory.current.path,
      source,
    );
    stdout.writeln('Saved ${project.packageSources.length} package source(s).');
    return 0;
  }

  Future<int> _addPackage(List<String> args) async {
    final spec = args.firstOrNull;
    if (spec == null) {
      throw StateError(
        'Usage: robotopia add package <id[@range]> [--project path]',
      );
    }
    final parts = spec.split('@');
    final dependency = ModDependency(
      id: parts.first,
      versionRange: VersionRange.parse(
        parts.length > 1 ? parts.sublist(1).join('@') : '*',
      ),
    );
    final project = await developerRepository.addProjectDependency(
      _option(args, '--project') ?? Directory.current.path,
      dependency,
    );
    stdout.writeln(
      'Saved ${project.dependencies.length} dependenc${project.dependencies.length == 1 ? 'y' : 'ies'}.',
    );
    return 0;
  }

  Future<int> _remove(List<String> args) async {
    if (args.firstOrNull != 'package' || args.length < 2) {
      throw StateError('Usage: robotopia remove package <id> [--project path]');
    }
    final project = await developerRepository.removeProjectDependency(
      _option(args, '--project') ?? Directory.current.path,
      args[1],
    );
    stdout.writeln('Saved ${project.dependencies.length} dependencies.');
    return 0;
  }

  Future<int> _list(List<String> args) async {
    if (args.firstOrNull != 'templates' && args.firstOrNull != 'sources') {
      throw StateError('Usage: robotopia list templates|sources');
    }
    if (args.first == 'templates') {
      stdout.writeln('mod');
      stdout.writeln('mod --unity-companion');
      stdout.writeln('asset-companion');
      stdout.writeln('unity-world');
      return 0;
    }
    final workspace = await developerRepository.loadDeveloperWorkspace(
      projectPath: _option(args, '--project'),
    );
    for (final source
        in workspace.project?.packageSources ?? const <PackageSource>[]) {
      stdout.writeln(
        '${source.enabled ? '[x]' : '[ ]'} ${source.id} ${source.url}',
      );
    }
    return 0;
  }

  Future<int> _resolve(List<String> args, {required bool restore}) async {
    final workspace = await developerRepository.resolveDeveloperProject(
      _option(args, '--project') ?? args.firstOrNull ?? Directory.current.path,
      restore: restore,
      includePrerelease: args.contains('--prerelease'),
    );
    stdout.writeln(
      '${restore ? 'Restored' : 'Resolved'} ${workspace.lock?.packages.length ?? 0} package(s).',
    );
    _printIssues(workspace.issues);
    return workspace.hasBlockingIssues ? 1 : 0;
  }

  Future<int> _pack(List<String> args) async {
    if (!await _ensureBuildTooling()) {
      return 1;
    }
    final packagePath = await developerRepository.packProject(
      _option(args, '--project') ?? Directory.current.path,
      outputDir: _option(args, '--output') ?? '',
      configuration: _option(args, '--configuration') ?? 'Release',
    );
    stdout.writeln(packagePath);
    return 0;
  }

  Future<int> _install(List<String> args) async {
    // Installing a prebuilt package is a consumer action (no toolchain needed); only the implicit pack path does.
    final provided = args.firstOrNull;
    if (provided == null && !await _ensureBuildTooling()) {
      return 1;
    }
    final packagePath =
        provided ??
        await developerRepository.packProject(Directory.current.path);
    final launcher = LocalLauncherRepository();
    final install = await launcher.detectKnownInstall();
    if (install == null) {
      throw StateError('Robotopia install was not detected.');
    }
    await launcher.installPackage(packagePath, install);
    stdout.writeln('Installed $packagePath');
    return 0;
  }

  Future<int> _launch(List<String> args, {required bool restart}) async {
    final launcher = LocalLauncherRepository();
    final snapshot = await launcher.loadSnapshot();
    final install = snapshot.gameInstall;
    if (install == null) {
      throw StateError('Robotopia install was not detected.');
    }
    final profile = snapshot.profiles.firstWhere(
      (item) => item.id == snapshot.selectedProfileId,
      orElse: () => snapshot.profiles.first,
    );
    final result = restart
        ? await launcher.restart(install, profile)
        : await launcher.launch(install, profile);
    stdout.writeln(result.message);
    return result.started ? 0 : 1;
  }

  Future<int> _migrate(List<String> args) async {
    if (args.firstOrNull != 'legacy') {
      throw StateError(
        'Usage: robotopia migrate legacy <gamePath> <outputRoot>',
      );
    }
    if (args.length < 3) {
      throw StateError(
        'Usage: robotopia migrate legacy <gamePath> <outputRoot>',
      );
    }
    final result = await developerRepository.migrateLegacyMods(
      args[1],
      args[2],
    );
    stdout.writeln(
      'Created ${result.createdProjects.length} project(s) in ${result.outputRoot}.',
    );
    _printIssues(result.issues);
    return result.issues.any((issue) => issue.isBlocking) ? 1 : 0;
  }

  int _unknown(String command) {
    stderr.writeln('Unknown command: $command');
    _printHelp();
    return 1;
  }

  String? _option(List<String> args, String name) {
    final index = args.indexOf(name);
    if (index < 0 || index + 1 >= args.length) {
      return null;
    }
    return args[index + 1];
  }

  void _printIssues(List<LauncherIssue> issues) {
    for (final issue in issues) {
      stdout.writeln('${issue.severity.name}: ${issue.message}');
    }
  }

  void _printHelp() {
    stdout.writeln('QuantumWorks CLI');
    stdout.writeln('Commands:');
    stdout.writeln(
      '  robotopia new mod <id> [--name name] [--unity-companion]',
    );
    stdout.writeln('  robotopia new unity-world <name> [--dir path]');
    stdout.writeln('  robotopia check project [path]');
    stdout.writeln('  robotopia check package <path>');
    stdout.writeln('  robotopia add source <id> <url>');
    stdout.writeln('  robotopia list sources|templates');
    stdout.writeln('  robotopia add package <id[@range]>');
    stdout.writeln('  robotopia remove package <id>');
    stdout.writeln('  robotopia resolve [--prerelease]');
    stdout.writeln('  robotopia restore [--prerelease]');
    stdout.writeln('  robotopia pack [--output dir]');
    stdout.writeln('  robotopia install [package]');
    stdout.writeln('  robotopia launch');
    stdout.writeln('  robotopia restart');
    stdout.writeln('  robotopia setup');
    stdout.writeln('  robotopia doctor [--strict]');
    stdout.writeln('  robotopia migrate legacy <gamePath> <outputRoot>');
    stdout.writeln('  robotopia projects list|add|remove|open');
    stdout.writeln(
      '  robotopia unity new-package|resolve|add|remove|list|repos|add-repo',
    );
    stdout.writeln(
      '  robotopia ugc publish --file <project.json> [--sync url] [--doc url]',
    );
    stdout.writeln(
      '  robotopia ugc watch <folder> [--sync url] [--doc url] [--scene id]',
    );
    stdout.writeln('  robotopia ugc status [--watch folder]');
    stdout.writeln('  robotopia ugc go-live');
  }
}

extension _FirstOrNull<T> on Iterable<T> {
  T? get firstOrNull {
    final iterator = this.iterator;
    return iterator.moveNext() ? iterator.current : null;
  }
}
