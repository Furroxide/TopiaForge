import 'dart:async';
import 'dart:convert';
import 'dart:io';

import 'package:crypto/crypto.dart';
import 'package:launcher_data/launcher_data.dart';
import 'package:launcher_domain/launcher_domain.dart';
import 'package:path/path.dart' as p;
import 'package:topiaforge/src/launcher_update_index_builder.dart';
import 'package:topiaforge/src/bounded_file_reader.dart';
import 'package:topiaforge/src/mod_registry_index_builder.dart';
import 'package:topiaforge/src/release_package_builder.dart';
import 'package:topiaforge/src/release_package_models.dart';
import 'package:topiaforge/src/release_package_validator.dart';
import 'package:topiaforge/src/registry_entry_builder.dart';
import 'package:topiaforge/src/release_metadata.dart';
import 'package:topiaforge/src/release_policy.dart';
import 'package:topiaforge/src/ugc_live_sync_transitions.dart';

part 'topiaforge_check_commands.dart';
part 'topiaforge_environment_commands.dart';
part 'topiaforge_help.dart';
part 'topiaforge_mod_commands.dart';
part 'topiaforge_new_commands.dart';
part 'topiaforge_registry_commands.dart';
part 'topiaforge_release_commands.dart';
part 'topiaforge_update_commands.dart';
part 'topiaforge_ugc_sidecar.dart';
part 'topiaforge_ugc_dev_commands.dart';
part 'topiaforge_ugc_unity_commands.dart';
part 'topiaforge_ui_bundle_commands.dart';
part 'topiaforge_unity_commands.dart';
part 'topiaforge_world_commands.dart';

Future<void> main(List<String> args) async {
  final cli = _TopiaForgeCli(LocalDeveloperRepository());
  try {
    final code = await cli.run(args);
    exitCode = code;
  } on UsageError catch (error) {
    stderr.writeln(error.message);
    exitCode = 2;
  } on Object catch (error) {
    // StateError.toString() prefixes "Bad state:", which reads like a crash;
    // the message alone is the actionable part.
    stderr.writeln(error is StateError ? error.message : error);
    exitCode = 1;
  }
}

/// A wrong-invocation error: printed as plain usage text (no "Bad state:"
/// noise) and distinguished from operation failures via exit code 2.
class UsageError implements Exception {
  UsageError(this.message);

  final String message;

  @override
  String toString() => message;
}

class _TopiaForgeCli {
  _TopiaForgeCli(this.developerRepository);

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
      'mod' => _mod(rest),
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
      'dev-install' => _devInstall(rest),
      'doctor' => _doctor(rest),
      'compat' => _compat(rest),
      'setup' => _setup(rest),
      'ugc' => _ugc(rest),
      'world' => _world(rest),
      'projects' => _projects(rest),
      'unity' => _unity(rest),
      'updates' => _updates(rest),
      'registry' => _registry(rest),
      'release' => _release(rest),
      _ => _unknown(command),
    };
  }

  Future<int> _add(List<String> args) async {
    return switch (args.firstOrNull) {
      'source' => _addSource(args.skip(1).toList()),
      'package' => _addPackage(args.skip(1).toList()),
      _ => throw UsageError('Usage: topiaforge add source|package ...'),
    };
  }

  Future<int> _addSource(List<String> args) async {
    if (args.length < 2) {
      throw UsageError(
        'Usage: topiaforge add source <name> <url> [--id id] [--project path]',
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
      throw UsageError(
        'Usage: topiaforge add package <id[@range]> [--project path]',
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
      throw UsageError(
        'Usage: topiaforge remove package <id> [--project path]',
      );
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
      throw UsageError('Usage: topiaforge list templates|sources');
    }
    if (args.first == 'templates') {
      final templates = await developerRepository.listModTemplates();
      for (final template in templates) {
        final label = template.label.isEmpty ? template.id : template.label;
        stdout.writeln(
          'mod --template ${template.id.padRight(10)} $label'
          '${template.description.isEmpty ? '' : ' — ${template.description}'}',
        );
      }
      stdout.writeln(
        'unity-world${' ' * 15} Unity 6 UGC authoring project with the companion package preinstalled',
      );
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
    if (args.contains('--all')) {
      final packed = await _packAllMods(
        outputDir: _option(args, '--output') ?? '',
        configuration: _option(args, '--configuration') ?? 'Release',
        includeDevMods: args.contains('--include-dev-mods'),
      );
      stdout.writeln('Packed ${packed.length} mod package(s).');
      return 0;
    }
    final projectPath = _option(args, '--project') ?? Directory.current.path;
    final outputDir = _option(args, '--output') ?? '';
    final configuration = _option(args, '--configuration') ?? 'Release';
    // A bare mod directory (manifest without topiaforge.project.json) packs
    // too, matching what the retired pack-mod.ps1 accepted.
    final hasProjectFile = File(
      p.join(projectPath, 'topiaforge.project.json'),
    ).existsSync();
    final hasManifest = File(
      p.join(projectPath, 'topiaforge.mod.json'),
    ).existsSync();
    final packagePath = !hasProjectFile && hasManifest
        ? await developerRepository.packModDirectory(
            projectPath,
            outputDir: outputDir,
            configuration: configuration,
          )
        : await developerRepository.packProject(
            projectPath,
            outputDir: outputDir,
            configuration: configuration,
          );
    stdout.writeln(packagePath);
    return 0;
  }

  /// Packs every first-party mod under `mods/`, keeping exactly one current
  /// package per mod id in the output directory (the launcher's bundled local
  /// source derives its catalog from that folder).
  Future<List<String>> _packAllMods({
    String outputDir = '',
    String configuration = 'Release',
    bool includeDevMods = false,
  }) async {
    final repoRoot = _findRepoRoot();
    if (repoRoot == null) {
      throw StateError(
        'The TopiaForge repository root was not found from '
        '${Directory.current.path}. Run from inside the TopiaForge '
        'repository, or pass --project to pack a single mod.',
      );
    }
    final output = Directory(
      outputDir.isEmpty ? p.join(repoRoot, 'dist') : outputDir,
    )..createSync(recursive: true);

    final projectDirs = <String>[];
    final modsDir = Directory(p.join(repoRoot, 'mods'));
    if (modsDir.existsSync()) {
      projectDirs.addAll(
        listBoundedDirectorySync(modsDir)
            .whereType<Directory>()
            .where(
              (dir) =>
                  File(p.join(dir.path, 'topiaforge.mod.json')).existsSync(),
            )
            .map((dir) => dir.path),
      );
    }
    final packed = <String>[];
    for (final dir in projectDirs) {
      final manifest = readBoundedJsonObjectSync(
        File(p.join(dir, 'topiaforge.mod.json')),
        maxBytes: CliFileLimits.manifest,
      );
      final name = (manifest['name'] as String?) ?? p.basename(dir);
      if (!includeDevMods && manifest['category'] == 'DevTool') {
        stdout.writeln(
          'Skipping dev-only mod $name (pass --include-dev-mods to pack it).',
        );
        continue;
      }

      // Drop any previously packed versions of this id so no superseded
      // build can be installed by mistake.
      final safeId = name.replaceAll(RegExp('[^A-Za-z0-9_.-]'), '_');
      for (final stale in listBoundedDirectorySync(output).whereType<File>()) {
        final staleName = p.basename(stale.path);
        if (staleName.startsWith('$safeId-') &&
            staleName.endsWith('.topiaforgemod')) {
          stale.deleteSync();
        }
      }

      final package = await developerRepository.packModDirectory(
        dir,
        outputDir: output.path,
        configuration: configuration,
      );
      final sha = sha256
          .convert(
            readBoundedRegularFileSync(
              File(package),
              maxBytes: CliFileLimits.package,
            ),
          )
          .toString();
      stdout.writeln('Packed $name (${manifest['version']}) sha256=$sha');
      packed.add(package);
    }
    return packed;
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
      throw StateError(_noInstallRemedy);
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
      throw StateError(_noInstallRemedy);
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

  int _unknown(String command) {
    stderr.writeln('Unknown command: $command');
    final suggestion = _suggestCommand(command);
    if (suggestion != null) {
      stderr.writeln('Did you mean: topiaforge $suggestion?');
    }
    stderr.writeln('Run `topiaforge help` for the command list.');
    return 2;
  }

  String? _option(List<String> args, String name) {
    final index = args.indexOf(name);
    if (index < 0 || index + 1 >= args.length) {
      return null;
    }
    return args[index + 1];
  }

  /// Collects every value of a repeatable flag (e.g. `--tag a --tag b`).
  List<String> _options(List<String> args, String name) {
    final values = <String>[];
    for (var index = 0; index < args.length - 1; index++) {
      if (args[index] == name) {
        values.add(args[index + 1]);
      }
    }
    return values;
  }

  void _printIssues(List<LauncherIssue> issues) {
    for (final issue in issues) {
      stdout.writeln('${issue.severity.name}: ${issue.message}');
    }
  }
}

extension _FirstOrNull<T> on Iterable<T> {
  T? get firstOrNull {
    final iterator = this.iterator;
    return iterator.moveNext() ? iterator.current : null;
  }
}
