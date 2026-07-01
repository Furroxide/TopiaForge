part of '../local_developer_repository.dart';

extension LocalDeveloperIoHelpers on LocalDeveloperRepository {
  Directory? _findProjectRoot(String startPath) {
    var current = FileSystemEntity.isDirectorySync(startPath)
        ? Directory(startPath).absolute
        : File(startPath).absolute.parent;
    while (true) {
      if (File(p.join(current.path, 'robotopia.project.json')).existsSync()) {
        return current;
      }
      final parent = current.parent;
      if (parent.path == current.path) {
        return null;
      }
      current = parent;
    }
  }

  Directory _requireProjectRoot(String projectPath) {
    final root = _findProjectRoot(projectPath);
    if (root == null) {
      throw StateError(
        'robotopia.project.json was not found from $projectPath',
      );
    }
    return root;
  }

  Future<DeveloperProject> _readProject(String root) async {
    final file = File(p.join(root, 'robotopia.project.json'));
    return DeveloperProject.fromJson(
      jsonDecode(await file.readAsString()) as Map<String, Object?>,
    );
  }

  Future<void> _writeProject(String root, DeveloperProject project) async {
    await File(
      p.join(root, 'robotopia.project.json'),
    ).writeAsString(_prettyJson(project.toJson()));
  }

  Future<DeveloperLock?> _readLock(String root) async {
    final file = File(p.join(root, 'robotopia.lock.json'));
    if (!file.existsSync()) {
      return null;
    }
    return DeveloperLock.fromJson(
      jsonDecode(await file.readAsString()) as Map<String, Object?>,
    );
  }

  Future<void> _writeLock(String root, DeveloperLock lock) async {
    await File(
      p.join(root, 'robotopia.lock.json'),
    ).writeAsString(_prettyJson(lock.toJson()));
  }

  Future<void> _writeStarterMod(
    String root,
    String id,
    String name,
    bool includeUnityCompanion,
  ) async {
    final assembly = _assemblyName(id);
    final abstractionsProject = p.relative(
      p.join(
        _repositoryRoot.path,
        'src',
        'Robotopia.Mods.Abstractions',
        'Robotopia.Mods.Abstractions.csproj',
      ),
      from: root,
    );
    await File(p.join(root, '$assembly.csproj')).writeAsString('''
<Project Sdk="Microsoft.NET.Sdk">
  <Import Project="robotopia.dev.props" Condition="Exists('robotopia.dev.props')" />
  <PropertyGroup>
    <TargetFramework>netstandard2.1</TargetFramework>
    <AssemblyName>$assembly</AssemblyName>
    <RootNamespace>$assembly</RootNamespace>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="$abstractionsProject" Private="false" />
  </ItemGroup>
</Project>
''');
    await File(p.join(root, 'robotopia.mod.json')).writeAsString(
      _prettyJson({
        'schemaVersion': 1,
        'id': id,
        'name': name,
        'version': '0.1.0',
        'author': '',
        'description': '',
        'entryAssembly': '$assembly.dll',
        'entryType': '$assembly.${_typeName(id)}Mod',
        'dependencies': <Object?>[],
        'apiAssemblies': <Object?>[],
        'license': 'MIT',
      }),
    );
    await File(p.join(root, '${_typeName(id)}Mod.cs')).writeAsString('''
using Robotopia.Mods;

namespace $assembly
{
    public sealed class ${_typeName(id)}Mod : IRobotopiaMod
    {
        public void OnLoad(IModContext context)
        {
            context.Logger.Info("$name loaded.");
        }

        public void OnUnload()
        {
        }
    }
}
''');
    if (includeUnityCompanion) {
      final companion = Directory(p.join(root, 'unity-companion'));
      companion.createSync(recursive: true);

      // Copy the authored UGC companion Unity package from the repo template when available (the CLI runs from
      // the repo). In synthetic environments (e.g. unit tests) the template may be absent; the README + sample
      // config are still written so the scaffold is self-describing either way.
      final templatePackage = Directory(
        p.join(
          _repositoryRoot.path,
          'templates',
          'Robotopia.ModTemplate',
          'unity-companion',
          'Packages',
          'com.robotopia.ugc-companion',
        ),
      );
      if (templatePackage.existsSync()) {
        _copyDirectory(
          templatePackage,
          Directory(
            p.join(companion.path, 'Packages', 'com.robotopia.ugc-companion'),
          ),
        );
      }

      await File(p.join(companion.path, 'README.md')).writeAsString(
        '# $name Unity Companion\n\n'
        'Open this folder as a Unity project, or copy `Packages/com.robotopia.ugc-companion` into an existing '
        'Unity project. Use **Robotopia → UGC Live Sync** to author UGC content and live-sync it into the '
        'running game with no restart. See docs/UgcLiveSync.md for the full workflow.\n',
      );

      // A sample of the runtime config the game mod reads (config/robotopia.ugc.livesync.json). Copy it there and
      // set the watch folder to share content between the Unity companion and the game.
      await File(
        p.join(companion.path, 'robotopia.ugc.livesync.sample.json'),
      ).writeAsString(
        _prettyJson(const UgcLiveSyncSettings().toRuntimeConfig()),
      );
    }
  }

  Future<void> _ensureProjectGitignore(String root) async {
    final file = File(p.join(root, '.gitignore'));
    final entries = [
      '.robotopia/packages/',
      '.robotopia/cache/',
      'robotopia.dev.props',
    ];
    final existing = file.existsSync() ? await file.readAsString() : '';
    final buffer = StringBuffer(existing);
    if (existing.isNotEmpty && !existing.endsWith('\n')) {
      buffer.writeln();
    }
    for (final entry in entries) {
      if (!existing.split('\n').map((line) => line.trim()).contains(entry)) {
        buffer.writeln(entry);
      }
    }
    await file.writeAsString(buffer.toString());
  }

  Future<void> _writeDevProps(String root, DeveloperLock lock) async {
    final references = <String>[];
    for (final package in lock.packages) {
      for (final assembly in package.apiAssemblies) {
        final hintPath = p.join(
          root,
          '.robotopia',
          'packages',
          package.id,
          package.version,
          'extracted',
          assembly,
        );
        references.add('''
    <Reference Include="${p.basenameWithoutExtension(assembly)}">
      <HintPath>$hintPath</HintPath>
      <Private>false</Private>
    </Reference>''');
      }
    }
    await File(p.join(root, 'robotopia.dev.props')).writeAsString('''
<Project>
  <ItemGroup>
${references.join('\n')}
  </ItemGroup>
</Project>
''');
  }

  Future<String> _migrateLegacyDll(File dll, String outputRoot) async {
    final id = 'legacy.${p.basenameWithoutExtension(dll.path).toLowerCase()}';
    final workspace = await createModProject(
      parentDirectory: outputRoot,
      id: id,
      name: p.basenameWithoutExtension(dll.path),
    );
    final root = Directory(workspace.projectRoot);
    dll.copySync(p.join(root.path, p.basename(dll.path)));
    return root.path;
  }

  Future<String> _migrateLegacyFolder(
    Directory source,
    String outputRoot,
  ) async {
    final manifest = ModManifest.fromJson(
      jsonDecode(
            await File(
              p.join(source.path, 'robotopia.mod.json'),
            ).readAsString(),
          )
          as Map<String, Object?>,
    );
    final root = Directory(p.join(outputRoot, _safeName(manifest.id)));
    if (root.existsSync()) {
      root.deleteSync(recursive: true);
    }
    _copyDirectory(source, root);
    await _writeProject(
      root.path,
      DeveloperProject(schemaVersion: 1, id: manifest.id, name: manifest.name),
    );
    await _ensureProjectGitignore(root.path);
    return root.path;
  }

  void _copyDirectory(Directory source, Directory destination) {
    destination.createSync(recursive: true);
    for (final entity in source.listSync(recursive: true)) {
      final relative = p.relative(entity.path, from: source.path);
      final target = p.join(destination.path, relative);
      if (entity is Directory) {
        Directory(target).createSync(recursive: true);
      } else if (entity is File) {
        File(target).createSync(recursive: true);
        entity.copySync(target);
      }
    }
  }

  Future<String> _which(String executable) async {
    final command = Platform.isWindows ? 'where' : 'which';
    final result = await Process.run(command, [executable]);
    if (result.exitCode != 0) {
      return '';
    }
    return result.stdout.toString().trim().split('\n').first.trim();
  }

  Future<String> _findUnityHub() async {
    final env = Platform.environment['UNITY_HUB_PATH'];
    if (env != null && env.isNotEmpty && File(env).existsSync()) {
      return env;
    }
    return _which(Platform.isWindows ? 'Unity Hub.exe' : 'unityhub');
  }

  Future<String> _findUnityEditor(DeveloperProject? project) async {
    final env = Platform.environment['UNITY_EDITOR_PATH'];
    if (env != null && env.isNotEmpty && File(env).existsSync()) {
      return env;
    }
    final configured = project?.unityCompanion.unityVersion ?? '';
    if (configured.isNotEmpty) {
      return configured;
    }
    return _which(Platform.isWindows ? 'Unity.exe' : 'Unity');
  }

  // Locates the UGC Automerge sidecar script. Prefers the resolved repo root (correct when the launcher runs
  // from a packaged location), then walks up from the current directory for parity with the CLI.
  String? _findSidecar() {
    final fromRepo = p.join(
      _repositoryRoot.path,
      'tools',
      'ugc-automerge-sidecar',
      'index.mjs',
    );
    if (File(fromRepo).existsSync()) {
      return fromRepo;
    }

    var dir = Directory.current.absolute;
    while (true) {
      final candidate = File(
        p.join(dir.path, 'tools', 'ugc-automerge-sidecar', 'index.mjs'),
      );
      if (candidate.existsSync()) {
        return candidate.path;
      }
      final parent = dir.parent;
      if (parent.path == dir.path) {
        return null;
      }
      dir = parent;
    }
  }

  // Runs `npm install` in the sidecar folder. Captures output (does not stream to stdout) so it is safe to call
  // from the launcher GUI; returns an action-log line plus an optional non-fatal issue.
  Future<({String action, LauncherIssue? issue})> _installSidecarDeps(
    String sidecarDir,
  ) async {
    try {
      final result = await Process.run(
        'npm',
        ['install', '--no-fund', '--no-audit'],
        workingDirectory: sidecarDir,
        runInShell: true,
      );
      if (result.exitCode == 0) {
        return (
          action: 'Installed UGC Automerge sidecar dependencies.',
          issue: null,
        );
      }
      return (
        action: 'npm install exited with code ${result.exitCode}.',
        issue: LauncherIssue(
          severity: IssueSeverity.warning,
          message:
              'npm install failed (exit ${result.exitCode}) in $sidecarDir.',
        ),
      );
    } on ProcessException catch (error) {
      return (
        action: 'Could not run npm.',
        issue: LauncherIssue(
          severity: IssueSeverity.warning,
          message: 'Could not run npm (${error.message}). Install Node.js 20+.',
        ),
      );
    }
  }

  String _assemblyName(String id) => id
      .split(RegExp(r'[^A-Za-z0-9]+'))
      .where((part) => part.isNotEmpty)
      .map((part) => part[0].toUpperCase() + part.substring(1))
      .join();

  String _typeName(String id) {
    final name = _assemblyName(id);
    return name.isEmpty ? 'Robotopia' : name;
  }

  String _safeName(String raw) =>
      raw.replaceAll(RegExp(r'[^A-Za-z0-9_.-]+'), '_');

  String _prettyJson(Object? value) =>
      const JsonEncoder.withIndent('  ').convert(value);
}

String _defaultDeveloperDataRoot() {
  final appData = Platform.environment['APPDATA'];
  if (Platform.isWindows && appData != null && appData.isNotEmpty) {
    return p.join(appData, 'RobotopiaLauncher');
  }
  final home =
      Platform.environment['HOME'] ??
      Platform.environment['USERPROFILE'] ??
      Directory.current.path;
  return p.join(home, '.robotopia_launcher');
}

String _findDeveloperRepoRoot() {
  var current = Directory.current.absolute;
  while (true) {
    if (File(p.join(current.path, 'RobotopiaModManager.slnx')).existsSync()) {
      return current.path;
    }
    final parent = current.parent;
    if (parent.path == current.path) {
      return Directory.current.absolute.path;
    }
    current = parent;
  }
}
