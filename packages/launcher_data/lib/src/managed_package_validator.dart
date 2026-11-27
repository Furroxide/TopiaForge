import 'dart:convert';
import 'dart:io';

import 'package:path/path.dart' as p;

import 'bounded_process.dart';
import 'dotnet_sdk.dart';

enum ManagedPackageValidation { package, themes, prepareThemes }

Future<List<String>> runManagedPackageValidator(
  Directory packageRoot, {
  required Directory repositoryRoot,
  required Iterable<Directory> searchRoots,
  RepositoryDotnetSdkResolver resolveDotnet = resolveRepositoryDotnetSdk,
  ManagedPackageValidation mode = ManagedPackageValidation.package,
}) async {
  final validator = _resolveManagedPackageValidator(
    repositoryRoot,
    searchRoots,
  );
  final hostRoot = _findValidatorHostRoot(validator);
  final dotnet = await resolveDotnet(hostRoot);
  try {
    final result = await runBoundedProcess(
      dotnet.executable,
      [
        validator.path,
        if (mode == ManagedPackageValidation.themes) '--ui-themes-only',
        if (mode == ManagedPackageValidation.prepareThemes)
          '--prepare-ui-themes',
        packageRoot.path,
      ],
      workingDirectory: hostRoot.path,
      runInShell: false,
      timeout: const Duration(seconds: 30),
      maxStdoutBytes: 1024 * 1024,
      maxStderrBytes: 4 * 1024 * 1024,
    );
    if (result.exitCode == 0) return const [];
    final details = <String>[
      ...const LineSplitter().convert(result.stderr),
      ...const LineSplitter().convert(result.stdout),
    ].map((line) => line.trim()).where((line) => line.isNotEmpty).toList();
    return details.isEmpty
        ? const [
            'TFPKG160: managed package metadata validation failed without details.',
          ]
        : details;
  } on BoundedProcessException catch (error) {
    return ['TFPKG161: managed package validator could not complete: $error'];
  }
}

File _resolveManagedPackageValidator(
  Directory repositoryRoot,
  Iterable<Directory> searchRoots,
) {
  const assembly = 'TopiaForge.ModPackageValidator.dll';
  final override = Platform.environment['TOPIAFORGE_PACKAGE_VALIDATOR_PATH'];
  final installed = p.join('tools', 'package-validator', assembly);
  final candidates = <File>[
    if (override != null && override.trim().isNotEmpty)
      File(p.normalize(p.absolute(override))),
    File(p.join(repositoryRoot.path, installed)),
    File(
      p.join(
        repositoryRoot.path,
        'src',
        'TopiaForge.ModPackageValidator',
        'bin',
        'Release',
        'net10.0',
        assembly,
      ),
    ),
    File(p.join(repositoryRoot.path, 'dist', 'TopiaForge', installed)),
  ];
  for (final seed in searchRoots) {
    var current = seed.absolute;
    for (var depth = 0; depth < 8; depth++) {
      candidates.add(File(p.join(current.path, installed)));
      final parent = current.parent;
      if (parent.path == current.path) break;
      current = parent;
    }
  }
  for (final candidate in candidates) {
    if (FileSystemEntity.typeSync(candidate.path, followLinks: false) ==
        FileSystemEntityType.file) {
      return candidate.absolute;
    }
  }
  throw StateError(
    'TFPKG150: the managed package validator is missing. Repair the '
    'TopiaForge release before installing mods.',
  );
}

Directory _findValidatorHostRoot(File validator) {
  var current = validator.parent.absolute;
  while (true) {
    final global = File(p.join(current.path, 'global.json'));
    if (FileSystemEntity.typeSync(global.path, followLinks: false) ==
        FileSystemEntityType.file) {
      return current;
    }
    final parent = current.parent;
    if (parent.path == current.path) {
      throw StateError(
        'TFPKG151: global.json is missing beside the package validator. '
        'Repair the TopiaForge release.',
      );
    }
    current = parent;
  }
}
