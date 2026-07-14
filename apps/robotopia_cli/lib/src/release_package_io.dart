import 'dart:convert';
import 'dart:io';

import 'package:archive/archive.dart';
import 'package:path/path.dart' as p;

import 'release_package_models.dart';

class ReleaseProcessRunner {
  const ReleaseProcessRunner();

  Future<void> runChecked(
    String executable,
    List<String> arguments, {
    String? workingDirectory,
    Map<String, String>? environment,
    bool runInShell = false,
  }) async {
    stdout.writeln('> $executable ${arguments.join(' ')}');
    final process = await Process.start(
      executable,
      arguments,
      workingDirectory: workingDirectory,
      environment: environment,
      runInShell: runInShell,
    );
    final out = stdout.addStream(process.stdout);
    final err = stderr.addStream(process.stderr);
    final code = await process.exitCode;
    await Future.wait([out, err]);
    if (code != 0) {
      throw StateError('$executable failed with exit code $code.');
    }
  }

  Future<bool> commandExists(String executable) async {
    final command = Platform.isWindows ? 'where' : 'which';
    final result = await Process.run(command, [executable]);
    return result.exitCode == 0;
  }
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

  void copyDirectory(Directory source, Directory destination) {
    if (!source.existsSync()) {
      return;
    }
    destination.createSync(recursive: true);
    for (final entity in source.listSync(recursive: true, followLinks: false)) {
      final relative = p.relative(entity.path, from: source.path);
      final target = p.join(destination.path, relative);
      if (entity is Directory) {
        Directory(target).createSync(recursive: true);
      } else if (entity is Link) {
        _copyLink(entity, target);
      } else if (entity is File) {
        File(target).parent.createSync(recursive: true);
        entity.copySync(target);
      }
    }
  }

  void copyDirectoryContents(Directory source, Directory destination) {
    if (!source.existsSync()) {
      return;
    }
    destination.createSync(recursive: true);
    for (final entity in source.listSync(followLinks: false)) {
      final target = p.join(destination.path, p.basename(entity.path));
      if (entity is Directory) {
        copyDirectory(entity, Directory(target));
      } else if (entity is Link) {
        _copyLink(entity, target);
      } else if (entity is File) {
        entity.copySync(target);
      }
    }
  }

  void copyFileIfExists(String source, String destination) {
    final file = File(source);
    if (!file.existsSync()) {
      return;
    }
    File(destination).parent.createSync(recursive: true);
    file.copySync(destination);
  }

  Future<void> copyMacBundle(String source, String destination) async {
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
    deleteIfExists(destination.path);
    destination.parent.createSync(recursive: true);
    if (platform == ReleasePackagePlatform.macos && Platform.isMacOS) {
      await processRunner.runChecked('/usr/bin/ditto', [
        '-c',
        '-k',
        '--sequesterRsrc',
        '--rsrc',
        '.',
        destination.path,
      ], workingDirectory: source.path);
      return;
    }
    if (platform == ReleasePackagePlatform.linux &&
        await processRunner.commandExists('zip')) {
      await processRunner.runChecked('zip', [
        '-q',
        '-r',
        destination.path,
        '.',
      ], workingDirectory: source.path);
      return;
    }
    _writeDartZip(source, destination);
  }

  Future<void> extractPlatformZip(
    File archiveFile,
    Directory destination,
    ReleasePackagePlatform platform,
  ) async {
    destination.createSync(recursive: true);
    if (platform == ReleasePackagePlatform.macos && Platform.isMacOS) {
      await processRunner.runChecked('/usr/bin/ditto', [
        '-x',
        '-k',
        archiveFile.path,
        destination.path,
      ]);
      return;
    }
    if (platform == ReleasePackagePlatform.linux &&
        await processRunner.commandExists('unzip')) {
      await processRunner.runChecked('unzip', [
        '-q',
        archiveFile.path,
        '-d',
        destination.path,
      ]);
      return;
    }
    _extractDartZip(archiveFile, destination);
  }

  void _copyLink(Link source, String destination) {
    File(destination).parent.createSync(recursive: true);
    try {
      Link(destination).createSync(source.targetSync(), recursive: true);
    } on FileSystemException {
      final resolved = File(source.resolveSymbolicLinksSync());
      if (resolved.existsSync()) {
        resolved.copySync(destination);
      }
    }
  }

  void _writeDartZip(Directory source, File destination) {
    final archive = Archive();
    for (final entity in source.listSync(recursive: true, followLinks: false)) {
      final relative = p.relative(entity.path, from: source.path);
      final name = _zipName(relative);
      if (entity is Directory) {
        archive.addFile(
          ArchiveFile.directory(name)..mode = _mode(entity.statSync(), 0x1ed),
        );
      } else if (entity is Link) {
        archive.addFile(
          ArchiveFile.symlink(name, entity.targetSync())..mode = 0x1ff,
        );
      } else if (entity is File) {
        archive.addFile(
          ArchiveFile.bytes(name, entity.readAsBytesSync())
            ..mode = _mode(entity.statSync(), 0x1a4),
        );
      }
    }
    destination.writeAsBytesSync(ZipEncoder().encode(archive));
  }

  void _extractDartZip(File archiveFile, Directory destination) {
    final archive = ZipDecoder().decodeBytes(archiveFile.readAsBytesSync());
    for (final entry in archive.files) {
      _rejectUnsafeArchiveName(entry.name);
      final output = p.normalize(p.join(destination.path, entry.name));
      if (!p.isWithin(destination.path, output) && output != destination.path) {
        throw StateError('Zip entry escapes the target: ${entry.name}');
      }
      if (entry.isDirectory) {
        Directory(output).createSync(recursive: true);
      } else if (entry.isSymbolicLink) {
        File(output).parent.createSync(recursive: true);
        Link(output).createSync(entry.symbolicLink!, recursive: true);
      } else {
        final bytes = entry.readBytes() ?? const <int>[];
        File(output)
          ..parent.createSync(recursive: true)
          ..writeAsBytesSync(bytes);
        if (!Platform.isWindows && (entry.unixPermissions & 0x49) != 0) {
          Process.runSync('chmod', ['+x', output]);
        }
      }
    }
  }

  int _mode(FileStat stat, int fallback) {
    final mode = stat.mode & 0x1ff;
    return mode == 0 ? fallback : mode;
  }

  String _zipName(String relativePath) =>
      p.posix.joinAll(p.split(relativePath));

  void _rejectUnsafeArchiveName(String name) {
    if (name.split('/').any((segment) => segment == '..')) {
      throw StateError('Zip entry has a path-traversal segment: $name');
    }
    if (p.isAbsolute(name)) {
      throw StateError('Zip entry must be relative: $name');
    }
  }
}

String releaseWarning(String message) => 'Warning: $message';

String singleLine(Object? value) => const LineSplitter()
    .convert(value?.toString() ?? '')
    .where((line) => line.trim().isNotEmpty)
    .join('\n');
