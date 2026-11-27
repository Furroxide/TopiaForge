import 'dart:convert';
import 'dart:io';

import 'package:archive/archive.dart';
import 'package:crypto/crypto.dart';
import 'package:launcher_domain/launcher_domain.dart';
import 'package:path/path.dart' as p;

import 'safe_zip_archive.dart';

part 'sdk_reference_pack_nuget.dart';

/// Package ids embedded in every complete TopiaForge V1 developer feed.
const topiaForgeSdkPackageIds = <String>{
  'TopiaForge.Mods.Abstractions',
  'TopiaForge.Mods.Analyzers',
  'TopiaForge.Mods.Chronos',
  'TopiaForge.Mods.CreatorContent',
  'TopiaForge.Mods.Interop.Unity',
  'TopiaForge.Mods.Multiplayer',
  'TopiaForge.Mods.Multiplayer.Generators',
  'TopiaForge.Mods.Prompts',
  'TopiaForge.Mods.RobotKit',
  'TopiaForge.Mods.Testing',
  'TopiaForge.Mods.Worlds',
};

/// SDK packages consumed exclusively through Roslyn's analyzer/source-generator path.
const topiaForgeAnalyzerPackageIds = <String>{
  'TopiaForge.Mods.Analyzers',
  'TopiaForge.Mods.Multiplayer.Generators',
};

/// Unity-free contract implementations supplied only to test runners by the testing package.
const topiaForgeTestingRuntimeSupportPackageIds = <String>{
  'TopiaForge.Mods.Abstractions',
  'TopiaForge.Mods.Chronos',
  'TopiaForge.Mods.CreatorContent',
  'TopiaForge.Mods.Multiplayer',
  'TopiaForge.Mods.Prompts',
  'TopiaForge.Mods.RobotKit',
  'TopiaForge.Mods.Worlds',
};

/// One NuGet package carried by a TopiaForge SDK feed.
final class SdkNuGetPackage {
  const SdkNuGetPackage({
    required this.id,
    required this.version,
    required this.path,
    required this.sha256,
    required this.kind,
    required this.dependencies,
    required this.runtimeAssemblyIds,
  });

  /// NuGet package identifier.
  final String id;

  /// Exact package version.
  final String version;

  /// Package path relative to the SDK-pack root.
  final String path;

  /// Lowercase SHA-256 of the complete nupkg.
  final String sha256;

  /// Package kind: `contract`, `testing`, or `analyzer`.
  final String kind;

  /// Exact package dependencies included in the nuspec.
  final List<String> dependencies;

  /// Assemblies intentionally supplied as test-runtime assets by this package.
  final List<String> runtimeAssemblyIds;

  /// Whether this package provides a compile-time contract assembly.
  bool get isContract => kind == 'contract' || kind == 'testing';

  /// Whether this package provides the runner-neutral testing implementation.
  bool get hasRuntimeAssembly => kind == 'testing';

  Map<String, Object?> toJson() => {
    'id': id,
    'version': version,
    'path': path,
    'sha256': sha256,
    'kind': kind,
    if (dependencies.isNotEmpty) 'dependencies': dependencies,
    if (runtimeAssemblyIds.isNotEmpty) 'runtimeAssemblies': runtimeAssemblyIds,
  };

  factory SdkNuGetPackage.fromJson(Map<String, Object?> json) =>
      SdkNuGetPackage(
        id: json['id'] as String? ?? '',
        version: json['version'] as String? ?? '',
        path: json['path'] as String? ?? '',
        sha256: json['sha256'] as String? ?? '',
        kind: json['kind'] as String? ?? '',
        dependencies: (json['dependencies'] as List? ?? const [])
            .map((value) => value.toString())
            .toList(growable: false),
        runtimeAssemblyIds: (json['runtimeAssemblies'] as List? ?? const [])
            .map((value) => value.toString())
            .toList(growable: false),
      );
}

/// A validated, immutable, local NuGet SDK feed on disk.
final class SdkReferencePack {
  const SdkReferencePack._({
    required this.root,
    required this.version,
    required this.gameVersion,
    required this.dotnetSdkVersion,
    required this.toolVersion,
    required this.targetFramework,
    required this.packages,
    required this.manifestSha256,
  });

  /// SDK-pack manifest filename.
  static const manifestName = 'sdk.json';

  /// Relative location of the flat local NuGet feed.
  static const feedPath = 'feed';

  static const maxManifestBytes = 1024 * 1024;
  static const maxFiles = 256;
  static const maxFileBytes = 128 * 1024 * 1024;

  /// Absolute SDK-pack root.
  final Directory root;

  /// Exact TopiaForge SDK package version.
  final String version;

  /// Exact Robotopia build targeted by this SDK/adaptor release.
  final String gameVersion;

  /// Exact .NET SDK expected by generated projects.
  final String dotnetSdkVersion;

  /// CLI version that produced the pack.
  final String toolVersion;

  /// Target framework supported by safe mod projects.
  final String targetFramework;

  /// Integrity-checked packages in this feed.
  final List<SdkNuGetPackage> packages;

  /// SHA-256 of the canonical SDK-pack manifest.
  final String manifestSha256;

  /// Absolute directory passed to NuGet as an additional package source.
  Directory get feed => Directory(p.join(root.path, feedPath));

  /// Returns whether [packageId] exists at the SDK's exact version.
  bool containsPackage(String packageId) => packages.any(
    (package) =>
        package.id.toLowerCase() == packageId.toLowerCase() &&
        package.version == version,
  );

  /// Loads and fully verifies an SDK feed without executing package code.
  static SdkReferencePack load(Directory root) {
    final manifest = File(p.join(root.path, manifestName));
    _requireRegularFile(manifest, maxBytes: maxManifestBytes);
    final manifestBytes = manifest.readAsBytesSync();
    final decoded = jsonDecode(utf8.decode(manifestBytes));
    if (decoded is! Map<String, Object?> || decoded['schemaVersion'] != 2) {
      throw StateError('SDK NuGet feed must use schemaVersion 2.');
    }

    final version = decoded['sdkVersion'] as String? ?? '';
    final gameVersion = decoded['gameVersion'] as String? ?? '';
    final dotnetSdkVersion = decoded['dotnetSdkVersion'] as String? ?? '';
    final toolVersion = decoded['toolVersion'] as String? ?? '';
    final targetFramework = decoded['targetFramework'] as String? ?? '';
    if (!_semanticVersion.hasMatch(version)) {
      throw StateError('SDK feed has an invalid SDK version.');
    }
    if (!_semanticVersion.hasMatch(gameVersion)) {
      throw StateError('SDK feed has an invalid game version.');
    }
    if (!_semanticVersion.hasMatch(dotnetSdkVersion)) {
      throw StateError('SDK feed has an invalid .NET SDK version.');
    }
    if (toolVersion.isNotEmpty && !_semanticVersion.hasMatch(toolVersion)) {
      throw StateError('SDK feed has an invalid tool version.');
    }
    if (targetFramework != 'netstandard2.1') {
      throw StateError('SDK feed targets unsupported $targetFramework.');
    }

    final packageJson = decoded['packages'];
    if (packageJson is! List || packageJson.isEmpty) {
      throw StateError('SDK feed has no NuGet packages.');
    }
    final packages = packageJson
        .map(
          (value) =>
              SdkNuGetPackage.fromJson((value as Map).cast<String, Object?>()),
        )
        .toList(growable: false);
    if (packages.length > maxFiles) {
      throw StateError('SDK feed has too many packages.');
    }

    final ids = <String>{};
    final declaredPaths = <String>{};
    for (final package in packages) {
      final idKey = package.id.toLowerCase();
      final relative = _safeRelativePath(package.path);
      if (!_packageId.hasMatch(package.id) ||
          package.version != version ||
          !_sha256Pattern.hasMatch(package.sha256) ||
          !const {'contract', 'testing', 'analyzer'}.contains(package.kind) ||
          !ids.add(idKey) ||
          !declaredPaths.add(relative) ||
          !relative.startsWith('$feedPath/')) {
        throw StateError('SDK package entry is invalid: ${package.id}');
      }
      for (final dependency in package.dependencies) {
        if (!_packageId.hasMatch(dependency) ||
            dependency.toLowerCase() == idKey) {
          throw StateError(
            'SDK package dependency is invalid: ${package.id} -> $dependency',
          );
        }
      }
      final runtimeIds = <String>{};
      for (final runtimeId in package.runtimeAssemblyIds) {
        if (!_packageId.hasMatch(runtimeId) ||
            !runtimeIds.add(runtimeId.toLowerCase())) {
          throw StateError(
            'SDK package runtime assembly is invalid: ${package.id} -> $runtimeId',
          );
        }
      }
      if ((package.kind == 'testing') !=
              package.runtimeAssemblyIds.contains(package.id) ||
          (package.kind != 'testing' &&
              package.runtimeAssemblyIds.isNotEmpty)) {
        throw StateError(
          'SDK package runtime inventory is invalid: ${package.id}',
        );
      }
      final file = File(p.join(root.path, p.fromUri(relative)));
      _requireRegularFile(file, maxBytes: maxFileBytes);
      final bytes = file.readAsBytesSync();
      if (sha256.convert(bytes).toString() != package.sha256) {
        throw StateError('SDK NuGet package failed integrity: $relative');
      }
      _validateNuGetPackage(package, bytes);
    }
    for (final package in packages) {
      for (final dependency in package.dependencies) {
        if (!ids.contains(dependency.toLowerCase())) {
          throw StateError(
            'SDK package ${package.id} requires missing package $dependency.',
          );
        }
      }
      for (final runtimeId in package.runtimeAssemblyIds) {
        if (!ids.contains(runtimeId.toLowerCase())) {
          throw StateError(
            'SDK package ${package.id} carries unknown runtime assembly $runtimeId.',
          );
        }
      }
    }
    if (!ids.contains('topiaforge.mods.abstractions')) {
      throw StateError('SDK feed is missing TopiaForge.Mods.Abstractions.');
    }

    final actualPaths = <String>{};
    for (final entity in root.listSync(recursive: true, followLinks: false)) {
      final type = FileSystemEntity.typeSync(entity.path, followLinks: false);
      if (type == FileSystemEntityType.directory) continue;
      if (type != FileSystemEntityType.file) {
        throw StateError(
          'SDK feed contains a link or special file: ${entity.path}',
        );
      }
      final relative = p.posix.joinAll(
        p.split(p.relative(entity.path, from: root.path)),
      );
      if (relative != manifestName) actualPaths.add(relative);
    }
    if (actualPaths.length != declaredPaths.length ||
        !actualPaths.containsAll(declaredPaths)) {
      throw StateError('SDK feed contains undeclared or missing files.');
    }

    return SdkReferencePack._(
      root: root.absolute,
      version: version,
      gameVersion: gameVersion,
      dotnetSdkVersion: dotnetSdkVersion,
      toolVersion: toolVersion,
      targetFramework: targetFramework,
      packages: List.unmodifiable(packages),
      manifestSha256: sha256.convert(manifestBytes).toString(),
    );
  }
}

/// Writes the reference-only NuGet feed shared by source builds and releases.
final class SdkReferencePackWriter {
  const SdkReferencePackWriter();

  SdkReferencePack write({
    required Directory destination,
    required String sdkVersion,
    String gameVersion = TopiaForgeRuntimeVersions.gameVersion,
    required String dotnetSdkVersion,
    String toolVersion = '',
    required Map<String, File> references,
    Map<String, File> documentation = const {},
    Map<String, File> analyzers = const {},
    Map<String, File> runtimeAssemblies = const {},
    Map<String, List<File>> runtimeSupportAssemblies = const {},
    Map<String, List<String>> packageDependencies = const {},
    Map<String, File> buildTransitiveProps = const {},
    Map<String, File> buildTransitiveTargets = const {},
  }) {
    if (!_semanticVersion.hasMatch(sdkVersion) ||
        !_semanticVersion.hasMatch(gameVersion) ||
        !_semanticVersion.hasMatch(dotnetSdkVersion) ||
        (toolVersion.isNotEmpty && !_semanticVersion.hasMatch(toolVersion))) {
      throw StateError(
        'SDK, .NET SDK, and tool versions must be exact SemVer values.',
      );
    }
    if (references.isEmpty ||
        !references.keys.any(
          (name) => name == 'TopiaForge.Mods.Abstractions',
        )) {
      throw StateError('An SDK feed needs TopiaForge.Mods.Abstractions.');
    }
    final type = FileSystemEntity.typeSync(
      destination.path,
      followLinks: false,
    );
    if (type == FileSystemEntityType.link) {
      throw StateError('Refusing to replace a linked SDK feed.');
    }
    if (type != FileSystemEntityType.notFound) {
      destination.deleteSync(recursive: true);
    }
    destination.createSync(recursive: true);

    final entries = <SdkNuGetPackage>[];
    final knownIds = {...references.keys, ...analyzers.keys};
    for (final dependencies in packageDependencies.values) {
      for (final dependency in dependencies) {
        if (!knownIds.contains(dependency)) {
          throw StateError('SDK package dependency is missing: $dependency');
        }
      }
    }

    for (final entry
        in references.entries.toList()
          ..sort((left, right) => left.key.compareTo(right.key))) {
      final packageId = _safePackageId(entry.key);
      final docs = documentation[packageId];
      if (docs == null) {
        throw StateError('$packageId XML documentation is missing.');
      }
      final runtime = runtimeAssemblies[packageId];
      final runtimeSupport = List<File>.of(
        runtimeSupportAssemblies[packageId] ?? const [],
      );
      if (runtime == null && runtimeSupport.isNotEmpty) {
        throw StateError(
          '$packageId cannot carry runtime support without its own runtime assembly.',
        );
      }
      final runtimeIds = <String>[];
      if (runtime != null) runtimeIds.add(packageId);
      for (final support in runtimeSupport) {
        if (p.extension(support.path).toLowerCase() != '.dll') {
          throw StateError('$packageId runtime support must be a managed DLL.');
        }
        final supportId = _safePackageId(
          p.basenameWithoutExtension(support.path),
        );
        if (supportId == packageId || !knownIds.contains(supportId)) {
          throw StateError(
            '$packageId has invalid runtime support assembly $supportId.',
          );
        }
        runtimeIds.add(supportId);
      }
      if (runtimeIds.toSet().length != runtimeIds.length) {
        throw StateError(
          '$packageId has duplicate runtime support assemblies.',
        );
      }
      runtimeSupport.sort(
        (left, right) =>
            p.basename(left.path).compareTo(p.basename(right.path)),
      );
      final kind = runtime == null ? 'contract' : 'testing';
      final dependencies = List<String>.of(
        packageDependencies[packageId] ?? const [],
      )..sort();
      final bytes = _contractNuGetPackage(
        packageId: packageId,
        version: sdkVersion,
        reference: entry.value,
        documentation: docs,
        runtime: runtime,
        runtimeSupport: runtimeSupport,
        dependencies: dependencies,
        customProps: buildTransitiveProps[packageId],
        customTargets: buildTransitiveTargets[packageId],
      );
      entries.add(
        _writePackage(
          destination,
          packageId,
          sdkVersion,
          bytes,
          kind,
          dependencies,
          runtimeIds,
        ),
      );
    }

    for (final entry
        in analyzers.entries.toList()
          ..sort((left, right) => left.key.compareTo(right.key))) {
      final packageId = _safePackageId(entry.key);
      final bytes = _analyzerNuGetPackage(
        packageId: packageId,
        version: sdkVersion,
        analyzer: entry.value,
        customProps: buildTransitiveProps[packageId],
        customTargets: buildTransitiveTargets[packageId],
      );
      entries.add(
        _writePackage(
          destination,
          packageId,
          sdkVersion,
          bytes,
          'analyzer',
          const [],
          const [],
        ),
      );
    }
    entries.sort((left, right) => left.id.compareTo(right.id));

    final manifest = <String, Object?>{
      'schemaVersion': 2,
      'sdkVersion': sdkVersion,
      'gameVersion': gameVersion,
      'dotnetSdkVersion': dotnetSdkVersion,
      if (toolVersion.isNotEmpty) 'toolVersion': toolVersion,
      'targetFramework': 'netstandard2.1',
      'packages': entries.map((entry) => entry.toJson()).toList(),
    };
    File(
      p.join(destination.path, SdkReferencePack.manifestName),
    ).writeAsStringSync(
      '${const JsonEncoder.withIndent('  ').convert(manifest)}\n',
      flush: true,
    );
    return SdkReferencePack.load(destination);
  }
}
