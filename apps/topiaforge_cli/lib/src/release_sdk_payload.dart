import 'dart:convert';
import 'dart:io';

import 'package:launcher_data/launcher_data.dart';
import 'package:path/path.dart' as p;

import 'bounded_file_reader.dart';

/// Builds the versioned, compile-only SDK payload embedded in every release.
final class ReleaseSdkPayloadWriter {
  const ReleaseSdkPayloadWriter({
    required this.repositoryRoot,
    required this.configuration,
  });

  final String repositoryRoot;
  final String configuration;

  SdkReferencePack write(String destinationRoot) {
    final projects = _sdkProjects();
    final abstractions = projects.singleWhere(
      (project) => project.name == 'TopiaForge.Mods.Abstractions',
    );
    final sdkVersion = _projectVersion(abstractions.projectFile);
    final dotnetSdkVersion = _dotnetSdkVersion();
    final toolVersion = _toolVersion();
    final references = <String, File>{};
    final documentation = <String, File>{};
    final analyzers = <String, File>{};
    final runtimeAssemblies = <String, File>{};
    final implementationAssemblies = <String, File>{};
    final packageDependencies = <String, List<String>>{};
    final buildTransitiveProps = <String, File>{};
    final buildTransitiveTargets = <String, File>{};

    for (final project in projects) {
      final packageVersion = _projectVersion(project.projectFile);
      if (packageVersion != sdkVersion) {
        throw StateError(
          '${project.name} has package version $packageVersion; every V1 SDK '
          'package must use the exact version $sdkVersion.',
        );
      }
      final props = File(
        p.join(
          project.projectFile.parent.path,
          'buildTransitive',
          '${project.name}.props',
        ),
      );
      final targets = File(
        p.join(
          project.projectFile.parent.path,
          'buildTransitive',
          '${project.name}.targets',
        ),
      );
      if (props.existsSync()) buildTransitiveProps[project.name] = props;
      if (targets.existsSync()) buildTransitiveTargets[project.name] = targets;
      if (project.isAnalyzer) {
        analyzers[project.name] = _builtImplementationAssembly(project);
        continue;
      }
      references[project.name] = _builtReferenceAssembly(project);
      final implementation = _builtImplementationAssembly(project);
      implementationAssemblies[project.name] = implementation;
      final xml = File(p.setExtension(implementation.path, '.xml'));
      if (!xml.existsSync()) {
        throw StateError(
          '${project.name} XML documentation is missing. Build the release '
          'with GenerateDocumentationFile enabled.',
        );
      }
      documentation[project.name] = xml;
      if (project.name == 'TopiaForge.Mods.Testing') {
        runtimeAssemblies[project.name] = implementation;
      }
      packageDependencies[project.name] = _projectDependencies(project);
    }
    final testingRuntimeSupport = <File>[];
    for (final packageId in topiaForgeTestingRuntimeSupportPackageIds) {
      final implementation = implementationAssemblies[packageId];
      if (implementation == null) {
        throw StateError(
          'Testing runtime support assembly is missing for $packageId.',
        );
      }
      testingRuntimeSupport.add(implementation);
    }

    final sdkRoot = Directory(p.join(destinationRoot, 'sdk'));
    if (sdkRoot.existsSync()) sdkRoot.deleteSync(recursive: true);
    final pack = const SdkReferencePackWriter().write(
      destination: Directory(p.join(sdkRoot.path, sdkVersion)),
      sdkVersion: sdkVersion,
      dotnetSdkVersion: dotnetSdkVersion,
      toolVersion: toolVersion,
      references: references,
      documentation: documentation,
      analyzers: analyzers,
      runtimeAssemblies: runtimeAssemblies,
      runtimeSupportAssemblies: {
        'TopiaForge.Mods.Testing': testingRuntimeSupport,
      },
      packageDependencies: packageDependencies,
      buildTransitiveProps: buildTransitiveProps,
      buildTransitiveTargets: buildTransitiveTargets,
    );
    sdkRoot.createSync(recursive: true);
    File(p.join(sdkRoot.path, 'index.json')).writeAsStringSync(
      '${const JsonEncoder.withIndent('  ').convert({
        'schemaVersion': 1,
        'defaultVersion': sdkVersion,
        'toolVersion': toolVersion,
        'versions': {
          sdkVersion: {'manifestSha256': pack.manifestSha256},
        },
      })}\n',
      flush: true,
    );
    _copyManagedPackageValidator(destinationRoot);
    File(
      p.join(repositoryRoot, 'global.json'),
    ).copySync(p.join(destinationRoot, 'global.json'));
    return pack;
  }

  void _copyManagedPackageValidator(String destinationRoot) {
    final source = Directory(
      p.join(
        repositoryRoot,
        'src',
        'TopiaForge.ModPackageValidator',
        'bin',
        configuration,
        'net10.0',
      ),
    );
    const files = [
      'TopiaForge.ModPackageValidator.dll',
      'TopiaForge.ModPackageValidator.deps.json',
      'TopiaForge.ModPackageValidator.runtimeconfig.json',
      'TopiaForge.ModManager.Core.dll',
    ];
    final destination = Directory(
      p.join(destinationRoot, 'tools', 'package-validator'),
    );
    if (destination.existsSync()) destination.deleteSync(recursive: true);
    destination.createSync(recursive: true);
    for (final name in files) {
      final input = File(p.join(source.path, name));
      if (!input.existsSync()) {
        throw StateError(
          'Managed package validator output is missing: ${input.path}. '
          'Build TopiaForge.ModPackageValidator in $configuration first.',
        );
      }
      input.copySync(p.join(destination.path, name));
    }
  }

  List<_SdkProject> _sdkProjects() {
    final source = Directory(p.join(repositoryRoot, 'src'));
    if (!source.existsSync()) {
      throw StateError(
        'Release source directory was not found: ${source.path}',
      );
    }
    final projects = <_SdkProject>[];
    for (final directory in source.listSync().whereType<Directory>()) {
      final name = p.basename(directory.path);
      if (!topiaForgeSdkPackageIds.contains(name)) {
        continue;
      }
      final project = File(p.join(directory.path, '$name.csproj'));
      if (!project.existsSync()) continue;
      final text = readBoundedTextFileSync(
        project,
        maxBytes: CliFileLimits.metadata,
      );
      final targetFramework = RegExp(
        r'<TargetFramework>\s*([^<]+?)\s*</TargetFramework>',
      ).firstMatch(text)?.group(1);
      if (targetFramework == null) {
        throw StateError('$name does not declare a TargetFramework.');
      }
      projects.add(
        _SdkProject(
          name: name,
          projectFile: project,
          isAnalyzer: topiaForgeAnalyzerPackageIds.contains(name),
          targetFramework: targetFramework,
        ),
      );
    }
    projects.sort((left, right) => left.name.compareTo(right.name));
    if (!projects.any(
      (project) => project.name == 'TopiaForge.Mods.Abstractions',
    )) {
      throw StateError('TopiaForge.Mods.Abstractions SDK project is missing.');
    }
    return projects;
  }

  File _builtReferenceAssembly(_SdkProject project) {
    final candidate = File(
      p.join(
        project.projectFile.parent.path,
        'obj',
        configuration,
        project.targetFramework,
        'ref',
        '${project.name}.dll',
      ),
    );
    if (!candidate.existsSync()) {
      throw StateError(
        'Reference assembly is missing for ${project.name}. Build with '
        'ProduceReferenceAssembly enabled.',
      );
    }
    return candidate;
  }

  File _builtImplementationAssembly(_SdkProject project) {
    final bin = Directory(
      p.join(project.projectFile.parent.path, 'bin', configuration),
    );
    if (!bin.existsSync()) {
      throw StateError('Release output is missing for ${project.name}.');
    }
    final candidates =
        bin
            .listSync(recursive: true, followLinks: false)
            .whereType<File>()
            .where((file) => p.basename(file.path) == '${project.name}.dll')
            .where((file) {
              final normalized = file.path.replaceAll('\\', '/').toLowerCase();
              return !normalized.contains('/ref/') &&
                  !normalized.contains('/publish/');
            })
            .toList()
          ..sort((left, right) => left.path.compareTo(right.path));
    if (candidates.isEmpty) {
      throw StateError('Release assembly is missing for ${project.name}.');
    }
    return candidates.first;
  }

  List<String> _projectDependencies(_SdkProject project) {
    final text = readBoundedTextFileSync(
      project.projectFile,
      maxBytes: CliFileLimits.metadata,
    );
    final dependencies = <String>{};
    for (final match in RegExp(
      r'<ProjectReference\s+Include="([^"]+)"',
    ).allMatches(text)) {
      final include = match.group(1)!.replaceAll('\\', '/');
      final name = p.basenameWithoutExtension(include);
      if (name.startsWith('TopiaForge.Mods.') &&
          !topiaForgeAnalyzerPackageIds.contains(name)) {
        dependencies.add(name);
      }
    }
    return dependencies.toList()..sort();
  }

  String _projectVersion(File project) {
    final text = readBoundedTextFileSync(
      project,
      maxBytes: CliFileLimits.metadata,
    );
    for (final property in const ['PackageVersion', 'Version']) {
      final match = RegExp(
        '<$property>\\s*([^<]+?)\\s*</$property>',
      ).firstMatch(text);
      if (match != null) return match.group(1)!.trim();
    }
    throw StateError('SDK project does not declare a package version.');
  }

  String _dotnetSdkVersion() {
    final global = readBoundedJsonObjectSync(
      File(p.join(repositoryRoot, 'global.json')),
      maxBytes: CliFileLimits.metadata,
    );
    final sdk = global['sdk'];
    final version = sdk is Map ? sdk['version'] : null;
    if (version is! String || version.trim().isEmpty) {
      throw StateError('global.json does not pin an exact .NET SDK.');
    }
    return version;
  }

  String _toolVersion() {
    final pubspec = readBoundedTextFileSync(
      File(p.join(repositoryRoot, 'apps', 'topiaforge_cli', 'pubspec.yaml')),
      maxBytes: CliFileLimits.metadata,
    );
    final match = RegExp(
      r'^version:\s*(\S+)\s*$',
      multiLine: true,
    ).firstMatch(pubspec);
    if (match == null) {
      throw StateError('TopiaForge CLI pubspec does not declare a version.');
    }
    return match.group(1)!;
  }
}

/// Release-time validation for the SDK pack and checkout-free templates.
final class ReleaseSdkPayloadValidator {
  const ReleaseSdkPayloadValidator();

  void validate(String payloadRoot) {
    final index = readBoundedJsonObjectSync(
      File(p.join(payloadRoot, 'sdk', 'index.json')),
      maxBytes: CliFileLimits.metadata,
    );
    if (index['schemaVersion'] != 1 || index['defaultVersion'] is! String) {
      throw StateError('Release SDK index is invalid.');
    }
    final version = index['defaultVersion']! as String;
    final pack = SdkReferencePack.load(
      Directory(p.join(payloadRoot, 'sdk', version)),
    );
    final versions = index['versions'];
    final selected = versions is Map ? versions[version] : null;
    final expectedSha = selected is Map ? selected['manifestSha256'] : null;
    if (pack.version != version ||
        expectedSha != pack.manifestSha256 ||
        index['toolVersion'] != pack.toolVersion) {
      throw StateError('Release SDK index does not match its reference pack.');
    }
    final packaged = pack.packages.map((package) => package.id).toSet();
    final missing = topiaForgeSdkPackageIds.difference(packaged);
    final unexpected = packaged.difference(topiaForgeSdkPackageIds);
    if (missing.isNotEmpty || unexpected.isNotEmpty) {
      throw StateError(
        'Release SDK package set is not canonical '
        '(missing: ${missing.join(', ')}; unexpected: ${unexpected.join(', ')}).',
      );
    }

    final templates = Directory(p.join(payloadRoot, 'templates', 'mod'));
    if (!templates.existsSync()) {
      throw StateError('Release mod templates are missing.');
    }
    final projectFiles = templates
        .listSync(recursive: true, followLinks: false)
        .whereType<File>()
        .where((file) => p.extension(file.path) == '.csproj')
        .toList();
    if (projectFiles.isEmpty) {
      throw StateError('Release mod templates contain no C# projects.');
    }
    final globalPath = p.join(payloadRoot, 'global.json');
    final global = readBoundedJsonObjectSync(
      File(globalPath),
      maxBytes: CliFileLimits.metadata,
    );
    final globalSdk = global['sdk'];
    if (globalSdk is! Map ||
        globalSdk['version'] != pack.dotnetSdkVersion ||
        globalSdk['rollForward'] != 'disable') {
      throw StateError(
        'Release global.json does not match the SDK toolchain lock.',
      );
    }
    for (final path in [
      p.join(
        payloadRoot,
        'tools',
        'package-validator',
        'TopiaForge.ModPackageValidator.dll',
      ),
      p.join(
        payloadRoot,
        'tools',
        'package-validator',
        'TopiaForge.ModPackageValidator.runtimeconfig.json',
      ),
      p.join(
        payloadRoot,
        'tools',
        'package-validator',
        'TopiaForge.ModPackageValidator.deps.json',
      ),
      p.join(
        payloadRoot,
        'tools',
        'package-validator',
        'TopiaForge.ModManager.Core.dll',
      ),
    ]) {
      if (!File(path).existsSync()) {
        throw StateError(
          'Release managed package validator is incomplete: $path',
        );
      }
    }
    for (final project in projectFiles) {
      final text = readBoundedTextFileSync(
        project,
        maxBytes: CliFileLimits.metadata,
      );
      if (text.contains('{{ABSTRACTIONS_PROJECT}}') ||
          text.contains('{{UNITYUI_PROJECT}}')) {
        throw StateError(
          'Release template depends on a source checkout: ${project.path}',
        );
      }
      final templateRelative = p.relative(project.path, from: templates.path);
      final templateParts = p.split(templateRelative);
      final templateRoot = p.join(templates.path, templateParts.first);
      for (final match in RegExp(
        r'<ProjectReference\s+Include="([^"]+)"',
      ).allMatches(text)) {
        final include = match.group(1)!.replaceAll('\\', p.separator);
        if (p.isAbsolute(include)) {
          throw StateError(
            'Release template has an absolute project reference: ${project.path}',
          );
        }
        final target = p.normalize(p.join(project.parent.path, include));
        if (!p.isWithin(templateRoot, target)) {
          throw StateError(
            'Release template project reference escapes its scaffold: ${project.path}',
          );
        }
      }
      final hasExactSdkVersion =
          text.contains('Version="${pack.version}"') ||
          text.contains('Version="{{SDK_VERSION}}"');
      if (!text.contains('PackageReference') ||
          !hasExactSdkVersion ||
          !text.contains('RestorePackagesWithLockFile')) {
        throw StateError(
          'Release template does not use exact locked SDK packages: '
          '${project.path}',
        );
      }
    }
  }
}

final class _SdkProject {
  const _SdkProject({
    required this.name,
    required this.projectFile,
    required this.isAnalyzer,
    required this.targetFramework,
  });

  final String name;
  final File projectFile;
  final bool isAnalyzer;
  final String targetFramework;
}
