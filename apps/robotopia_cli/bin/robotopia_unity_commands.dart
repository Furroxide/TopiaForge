part of 'robotopia.dart';

extension _RobotopiaUnityCommands on _RobotopiaCli {
  // Unity-side VPM from the terminal: scaffold packages, manage packages, and
  // manage listings.
  Future<int> _unity(List<String> args) async {
    final sub = args.firstOrNull;
    String pathArg(int index) =>
        args.length > index && !args[index].startsWith('-')
        ? args[index]
        : Directory.current.path;

    switch (sub) {
      case 'pack-packages':
        final packageDirectories = _options(args, '--package');
        final explicitPackages = packageDirectories.isNotEmpty;
        if (explicitPackages &&
            (_option(args, '--repo-id') == null ||
                _option(args, '--repo-name') == null ||
                _option(args, '--author') == null)) {
          throw UsageError(
            'Community VPM packaging requires --repo-id, --repo-name, and '
            '--author with one or more --package <dir> options.',
          );
        }
        final summary = await developerRepository.packUnityPackages(
          outputDir: _option(args, '--output') ?? '',
          packageDirectories: packageDirectories,
          repositoryId: _option(args, '--repo-id') ?? '',
          repositoryName: _option(args, '--repo-name') ?? '',
          repositoryAuthor: _option(args, '--author') ?? '',
        );
        summary.forEach(stdout.writeln);
        return 0;
      case 'new-package':
        if (args.length < 2) {
          stderr.writeln(
            'Usage: robotopia unity new-package <id> [--name Name] [--dir path]',
          );
          return 2;
        }
        final path = await developerRepository.createUnityPackage(
          parentDirectory: _option(args, '--dir') ?? Directory.current.path,
          id: args[1],
          name: _option(args, '--name') ?? '',
        );
        stdout.writeln('Created package ${args[1]} at $path.');
        return 0;
      case 'resolve':
        final resolved = await developerRepository.resolveUnityProject(
          pathArg(1),
          restore: !args.contains('--no-restore'),
        );
        stdout.writeln('Resolved ${resolved.length} package(s):');
        for (final pkg in resolved) {
          stdout.writeln('  ${pkg.id} ${pkg.version}');
        }
        return 0;
      case 'add':
        if (args.length < 2) {
          stderr.writeln('Usage: robotopia unity add <id[@range]> [path]');
          return 2;
        }
        final spec = args[1];
        final at = spec.indexOf('@');
        final id = at < 0 ? spec : spec.substring(0, at);
        final range = at < 0 ? '*' : spec.substring(at + 1);
        final resolved = await developerRepository.addUnityPackage(
          pathArg(2),
          id,
          range,
        );
        stdout.writeln(
          'Added $id ($range); ${resolved.length} package(s) resolved.',
        );
        return 0;
      case 'remove':
        if (args.length < 2) {
          stderr.writeln('Usage: robotopia unity remove <id> [path]');
          return 2;
        }
        await developerRepository.removeUnityPackage(pathArg(2), args[1]);
        stdout.writeln('Removed ${args[1]}.');
        return 0;
      case 'list':
        final available = await developerRepository
            .listAvailableUnityPackages();
        if (available.isEmpty) {
          stdout.writeln(
            'No packages available. Build dist/vpm with `robotopia unity pack-packages`.',
          );
        }
        for (final info in available) {
          stdout.writeln('  ${info.name} ${info.version} — ${info.label}');
        }
        return 0;
      case 'repos':
        for (final repo in await developerRepository.listUnityRepos()) {
          stdout.writeln(
            '  ${repo.enabled ? '[x]' : '[ ]'} ${repo.id} ${repo.url}',
          );
        }
        return 0;
      case 'add-repo':
        if (args.length < 2) {
          stderr.writeln('Usage: robotopia unity add-repo <index.json url>');
          return 2;
        }
        final repos = await developerRepository.addUnityRepo(args[1]);
        stdout.writeln('Subscribed; ${repos.length} repo(s).');
        return 0;
      case 'remove-repo':
        if (args.length < 2) {
          stderr.writeln('Usage: robotopia unity remove-repo <id>');
          return 2;
        }
        final repos = await developerRepository.removeUnityRepo(args[1]);
        stdout.writeln('Unsubscribed; ${repos.length} repo(s).');
        return 0;
      case 'build-ui-bundle':
        return _unityBuildUiBundle(args.skip(1).toList());
      case 'new-repo':
        stdout.writeln(
          'Run `robotopia unity pack-packages` to (re)generate dist/vpm/index.json from your com.robotopia.* '
          'packages. Community authors pass one or more `--package <dir>` options plus `--repo-id`, '
          '`--repo-name`, `--author`, and `--output`, then subscribe with '
          '`robotopia unity add-repo <path-to-index.json>`.',
        );
        return 0;
      default:
        stdout.writeln('Usage:');
        stdout.writeln(
          '  robotopia unity pack-packages [--output path] [--package dir ... '
          '--repo-id id --repo-name name --author author]',
        );
        stdout.writeln(
          '  robotopia unity new-package <id> [--name Name] [--dir path]',
        );
        stdout.writeln('  robotopia unity resolve [path] [--no-restore]');
        stdout.writeln('  robotopia unity add <id[@range]> [path]');
        stdout.writeln('  robotopia unity remove <id> [path]');
        stdout.writeln('  robotopia unity list');
        stdout.writeln(
          '  robotopia unity repos | add-repo <url> | remove-repo <id> | new-repo',
        );
        stdout.writeln(
          '  robotopia unity build-ui-bundle [--unity <editor>] [--rebuild] [--dry-run]',
        );
        return sub == null ? 0 : 1;
    }
  }

  // VCC-style multi-project registry from the terminal: list, add, remove, open.
  Future<int> _projects(List<String> args) async {
    final sub = args.firstOrNull;
    switch (sub) {
      case 'list':
        final projects = await developerRepository.listProjects();
        if (projects.isEmpty) {
          stdout.writeln('No projects tracked.');
        } else {
          for (final project in projects) {
            final unity = project.unityVersion.isEmpty
                ? ''
                : ' [Unity ${project.unityVersion}]';
            stdout.writeln(
              '${project.kind.name}: ${project.name}$unity — ${project.path}',
            );
          }
        }
        final editors = await developerRepository.listUnityEditors();
        if (editors.isNotEmpty) {
          stdout.writeln(
            'Unity editors: ${editors.map((e) => e.version).join(', ')}',
          );
        }
        return 0;
      case 'add':
        final path = args.length > 1 ? args[1] : Directory.current.path;
        final projects = await developerRepository.addExistingProject(path);
        stdout.writeln('Tracked $path (${projects.length} project(s) total).');
        return 0;
      case 'remove':
        if (args.length < 2) {
          stderr.writeln('Usage: robotopia projects remove <path>');
          return 2;
        }
        final projects = await developerRepository.removeProject(args[1]);
        stdout.writeln('Untracked ${args[1]} (${projects.length} remaining).');
        return 0;
      case 'open':
        final path = args.length > 1 ? args[1] : Directory.current.path;
        final editor = await developerRepository.openProjectInUnity(path);
        stdout.writeln('Opened $path in Unity ($editor).');
        return 0;
      default:
        stdout.writeln('Usage:');
        stdout.writeln('  robotopia projects list');
        stdout.writeln('  robotopia projects add [path]');
        stdout.writeln('  robotopia projects remove <path>');
        stdout.writeln('  robotopia projects open [path]');
        return sub == null ? 0 : 1;
    }
  }
}
