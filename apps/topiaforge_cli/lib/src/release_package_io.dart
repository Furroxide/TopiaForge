import 'dart:convert';
import 'dart:io';

import 'package:launcher_data/launcher_data.dart';
import 'package:path/path.dart' as p;

import 'release_archive_policy.dart';
import 'release_package_models.dart';

class ReleaseProcessRunner {
  const ReleaseProcessRunner();

  Future<ProcessResult> runResult(
    String executable,
    List<String> arguments, {
    String? workingDirectory,
    Map<String, String>? environment,
    bool runInShell = false,
  }) {
    return Process.run(
      executable,
      arguments,
      workingDirectory: workingDirectory,
      environment: releaseChildEnvironment(environment),
      includeParentEnvironment: false,
      runInShell: runInShell,
    );
  }

  Future<ProcessResult> runBoundedResult(
    String executable,
    List<String> arguments, {
    String? workingDirectory,
    Map<String, String>? environment,
    Duration timeout = const Duration(minutes: 2),
    int maxStdoutBytes = 2 * 1024 * 1024,
    int maxStderrBytes = 2 * 1024 * 1024,
  }) async {
    final result = await runBoundedProcess(
      executable,
      arguments,
      workingDirectory: workingDirectory,
      environment: releaseChildEnvironment(environment),
      includeParentEnvironment: false,
      timeout: timeout,
      maxStdoutBytes: maxStdoutBytes,
      maxStderrBytes: maxStderrBytes,
    );
    return ProcessResult(0, result.exitCode, result.stdout, result.stderr);
  }

  Future<void> runChecked(
    String executable,
    List<String> arguments, {
    String? workingDirectory,
    Map<String, String>? environment,
    bool runInShell = false,
    Set<String> redactedValueOptions = const {},
  }) async {
    stdout.writeln(
      formatCommandForLog(
        executable,
        arguments,
        redactedValueOptions: redactedValueOptions,
      ),
    );
    late Process process;
    try {
      process = await Process.start(
        executable,
        arguments,
        workingDirectory: workingDirectory,
        environment: releaseChildEnvironment(environment),
        includeParentEnvironment: false,
        runInShell: runInShell,
      );
    } on ProcessException catch (error) {
      if (redactedValueOptions.isEmpty) {
        rethrow;
      }
      throw StateError(
        '$executable could not be started (OS error ${error.errorCode}).',
      );
    }
    final out = stdout.addStream(process.stdout);
    final err = stderr.addStream(process.stderr);
    final code = await process.exitCode;
    await Future.wait([out, err]);
    if (code != 0) {
      throw StateError('$executable failed with exit code $code.');
    }
  }

  String formatCommandForLog(
    String executable,
    List<String> arguments, {
    Set<String> redactedValueOptions = const {},
  }) {
    final redacted = _redactCommandArguments(arguments, redactedValueOptions);
    return '> $executable ${redacted.join(' ')}';
  }

  Future<bool> commandExists(String executable) async =>
      await resolveCommand(executable) != null;

  /// Resolves [executable] to a launchable path, or null when it is not on
  /// PATH.
  ///
  /// Returning the resolved path matters on Windows, where the thing `where`
  /// finds is often `flutter.bat` or `dart.bat`. `Process.start` does not run a
  /// batch file unless it is named exactly or a shell is involved, so a caller
  /// that asked "does this exist?" and then launched the bare name got
  /// `ProcessException: The system cannot find the file specified` — a message
  /// that names neither the tool nor the reason.
  Future<String?> resolveCommand(String executable) async {
    final command = Platform.isWindows ? 'where' : 'which';
    final result = await runResult(command, [executable]);
    if (result.exitCode != 0) {
      return null;
    }
    // `where` reports every match, one per line, best first.
    final candidates = <String>[
      for (final line in const LineSplitter().convert(result.stdout.toString()))
        if (line.trim().isNotEmpty && File(line.trim()).existsSync()) line.trim(),
    ];
    if (candidates.isEmpty) {
      return null;
    }
    if (!Platform.isWindows) {
      return candidates.first;
    }
    // On Windows the first match is often the extensionless POSIX script that
    // ships beside the .bat - `flutter` next to `flutter.bat`. CreateProcess
    // cannot run it and fails with "%1 is not a valid Win32 application", so
    // prefer a candidate Windows will actually execute.
    final runnable = _windowsExecutableExtensions();
    for (final candidate in candidates) {
      if (runnable.contains(p.extension(candidate).toLowerCase())) {
        return candidate;
      }
    }
    return candidates.first;
  }

  /// The extensions Windows treats as directly executable, from PATHEXT.
  Set<String> _windowsExecutableExtensions() {
    final raw = Platform.environment['PATHEXT'] ?? '';
    final parsed = <String>{
      for (final entry in raw.split(';'))
        if (entry.trim().isNotEmpty) entry.trim().toLowerCase(),
    };
    // PATHEXT is user-editable and can be missing or trimmed down; these three
    // are what the tools this resolves actually ship.
    return parsed.isEmpty ? const {'.exe', '.bat', '.cmd'} : parsed;
  }

  List<String> _redactCommandArguments(
    List<String> arguments,
    Set<String> redactedValueOptions,
  ) {
    final redacted = <String>[];
    var redactNext = false;
    for (final argument in arguments) {
      if (redactNext) {
        redacted.add(_redactedArgument);
        redactNext = false;
        continue;
      }
      final equals = argument.indexOf('=');
      if (equals > 0) {
        final option = argument.substring(0, equals);
        if (redactedValueOptions.contains(option)) {
          redacted.add('$option=$_redactedArgument');
          continue;
        }
      }
      redacted.add(argument);
      redactNext = redactedValueOptions.contains(argument);
    }
    return redacted;
  }
}

Map<String, String> releaseChildEnvironment(Map<String, String>? overrides) {
  final result = Map<String, String>.of(Platform.environment);
  if (overrides != null) {
    result.addAll(overrides);
  }
  for (final name in result.keys.toList(growable: false)) {
    if (_releaseSensitiveEnvironmentNames.contains(name) ||
        _looksSensitiveEnvironmentName(name)) {
      result.remove(name);
    }
  }
  return result;
}

bool _looksSensitiveEnvironmentName(String name) {
  final normalized = name.toUpperCase();
  return normalized.contains('TOKEN') ||
      normalized.contains('SECRET') ||
      normalized.contains('PASSWORD') ||
      normalized.contains('PASSWD') ||
      normalized.contains('CREDENTIAL') ||
      normalized.contains('PRIVATE_KEY') ||
      normalized.contains('ACCESS_KEY') ||
      normalized.contains('AUTHORIZATION') ||
      normalized == 'API_KEY' ||
      normalized.endsWith('_API_KEY') ||
      normalized == 'DATABASE_URL' ||
      normalized.endsWith('_DATABASE_URL') ||
      normalized.endsWith('_DB_URL') ||
      normalized == 'SSH_AUTH_SOCK' ||
      RegExp(r'(^|_)PAT($|_)').hasMatch(normalized);
}

class ReleaseFileOps {
  const ReleaseFileOps({this.processRunner = const ReleaseProcessRunner()});

  final ReleaseProcessRunner processRunner;

  void deleteIfExists(String path) {
    final type = FileSystemEntity.typeSync(path, followLinks: false);
    if (type == FileSystemEntityType.notFound) {
      return;
    }
    if (type == FileSystemEntityType.directory) {
      Directory(path).deleteSync(recursive: true);
    } else {
      File(path).deleteSync();
    }
  }

  void copyDirectory(
    Directory source,
    Directory destination, {
    Set<String> excludedNames = const {},
    Set<String> excludedNamePrefixes = const {},
  }) {
    if (!_isRealSourceDirectory(source)) {
      return;
    }
    _copyDirectory(
      source,
      destination,
      sourceRoot: source.absolute.path,
      excludedNames: excludedNames,
      excludedNamePrefixes: excludedNamePrefixes,
    );
  }

  void _copyDirectory(
    Directory source,
    Directory destination, {
    required String sourceRoot,
    required Set<String> excludedNames,
    required Set<String> excludedNamePrefixes,
  }) {
    destination.createSync(recursive: true);
    final normalizedExclusions = excludedNames
        .map((name) => name.toLowerCase())
        .toSet();
    final normalizedPrefixes = excludedNamePrefixes
        .map((name) => name.toLowerCase())
        .toSet();
    for (final entity in source.listSync(followLinks: false)) {
      final name = p.basename(entity.path);
      final normalizedName = name.toLowerCase();
      if (normalizedExclusions.contains(normalizedName) ||
          normalizedPrefixes.any(normalizedName.startsWith)) {
        continue;
      }
      final target = p.join(destination.path, name);
      if (entity is Directory) {
        _copyDirectory(
          entity,
          Directory(target),
          sourceRoot: sourceRoot,
          excludedNames: normalizedExclusions,
          excludedNamePrefixes: normalizedPrefixes,
        );
      } else if (entity is Link) {
        _copyLink(entity, target, sourceRoot);
      } else if (entity is File) {
        File(target).parent.createSync(recursive: true);
        entity.copySync(target);
      }
    }
  }

  void copyDirectoryContents(Directory source, Directory destination) {
    if (!_isRealSourceDirectory(source)) {
      return;
    }
    destination.createSync(recursive: true);
    for (final entity in source.listSync(followLinks: false)) {
      final target = p.join(destination.path, p.basename(entity.path));
      if (entity is Directory) {
        _copyDirectory(
          entity,
          Directory(target),
          sourceRoot: source.absolute.path,
          excludedNames: const {},
          excludedNamePrefixes: const {},
        );
      } else if (entity is Link) {
        _copyLink(entity, target, source.absolute.path);
      } else if (entity is File) {
        entity.copySync(target);
      }
    }
  }

  void copyFileIfExists(String source, String destination) {
    if (FileSystemEntity.typeSync(source, followLinks: false) ==
        FileSystemEntityType.link) {
      throw StateError('Refusing to copy a linked release file: $source');
    }
    final file = File(source);
    if (!file.existsSync()) {
      return;
    }
    File(destination).parent.createSync(recursive: true);
    file.copySync(destination);
  }

  Future<void> copyMacBundle(String source, String destination) async {
    _archivePolicy.validateSourceLinks(
      Directory(source),
      allowContainedLinks: true,
    );
    deleteIfExists(destination);
    if (Platform.isMacOS &&
        await processRunner.commandExists('/usr/bin/ditto')) {
      await processRunner.runChecked('/usr/bin/ditto', [source, destination]);
      return;
    }
    copyDirectory(Directory(source), Directory(destination));
  }

  Future<void> setExecutableBit(String path) async {
    if (Platform.isWindows || !FileSystemEntity.isFileSync(path)) {
      return;
    }
    await processRunner.runChecked('chmod', ['+x', path]);
  }

  Future<void> writePlatformZip(
    Directory source,
    File destination,
    ReleasePackagePlatform platform,
  ) async {
    final allowContainedLinks = platform == ReleasePackagePlatform.macos;
    _archivePolicy.validateSourceLinks(
      source,
      allowContainedLinks: allowContainedLinks,
    );
    destination.parent.createSync(recursive: true);
    // One writer on every host keeps entry ordering, timestamps, permissions,
    // and metadata byte-for-byte reproducible across release runners.
    _archivePolicy.writeDartZip(
      source,
      destination,
      allowContainedLinks: allowContainedLinks,
    );
  }

  Future<void> extractPlatformZip(
    File archiveFile,
    Directory destination,
    ReleasePackagePlatform platform,
  ) async {
    _archivePolicy.extractDartZip(
      archiveFile,
      destination,
      allowContainedLinks: platform == ReleasePackagePlatform.macos,
    );
  }

  void _copyLink(Link source, String destination, String sourceRoot) {
    _archivePolicy.validateLink(source, Directory(sourceRoot));
    File(destination).parent.createSync(recursive: true);
    try {
      Link(destination).createSync(source.targetSync(), recursive: true);
    } on FileSystemException catch (error) {
      throw StateError(
        'Could not preserve the contained release link ${source.path}: '
        '${error.message}',
      );
    }
  }

  bool _isRealSourceDirectory(Directory source) {
    final type = FileSystemEntity.typeSync(source.path, followLinks: false);
    if (type == FileSystemEntityType.notFound) {
      return false;
    }
    if (type != FileSystemEntityType.directory) {
      throw StateError(
        'Release source must be a real directory: ${source.path}',
      );
    }
    return true;
  }
}

const _archivePolicy = ReleaseArchivePolicy();
const _redactedArgument = '<redacted>';
const _releaseSensitiveEnvironmentNames = {
  'GITHUB_TOKEN',
  'GH_TOKEN',
  'MACOS_CERTIFICATE_P12',
  'MACOS_CERTIFICATE_PASSWORD',
  'MACOS_DEVELOPER_ID_APPLICATION',
  'MACOS_NOTARY_APPLE_ID',
  'MACOS_NOTARY_PASSWORD',
  'MACOS_NOTARY_TEAM_ID',
  'ROBOTOPIA_REFS_TOKEN',
  'WINDOWS_CERTIFICATE_PFX',
  'WINDOWS_CERTIFICATE_PASSWORD',
};

String releaseWarning(String message) => 'Warning: $message';

String singleLine(Object? value) => const LineSplitter()
    .convert(value?.toString() ?? '')
    .where((line) => line.trim().isNotEmpty)
    .join('\n');
