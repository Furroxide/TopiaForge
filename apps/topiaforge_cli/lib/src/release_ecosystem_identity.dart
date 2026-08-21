import 'dart:convert';
import 'dart:io';

import 'package:crypto/crypto.dart';
import 'package:path/path.dart' as p;

import 'bounded_file_reader.dart';

class ReleaseEcosystemIdentity {
  const ReleaseEcosystemIdentity._();

  static String digestDirectory(Directory root) {
    if (FileSystemEntity.typeSync(root.path, followLinks: false) !=
        FileSystemEntityType.directory) {
      throw StateError(
        'Embedded canonical ecosystem must be a real directory.',
      );
    }
    final rootPath = p.normalize(root.absolute.path);
    final records = <String, String>{};
    final pending = <Directory>[root];
    var entryCount = 0;
    while (pending.isNotEmpty) {
      final directory = pending.removeLast();
      for (final entity in directory.listSync(followLinks: false)) {
        entryCount += 1;
        if (entryCount > CliFileLimits.maxDirectoryEntries) {
          throw StateError('Canonical ecosystem exceeds the entry limit.');
        }
        final type = FileSystemEntity.typeSync(entity.path, followLinks: false);
        if (type == FileSystemEntityType.directory) {
          pending.add(Directory(entity.path));
          continue;
        }
        if (type != FileSystemEntityType.file || entity is! File) {
          throw StateError(
            'Canonical ecosystem may contain only regular files and '
            'directories.',
          );
        }
        final relative = p
            .relative(entity.absolute.path, from: rootPath)
            .replaceAll(r'\', '/');
        final bytes = readBoundedRegularFileSync(
          entity,
          maxBytes: CliFileLimits.package,
          allowEmpty: true,
        );
        records[relative] = sha256.convert(bytes).toString();
      }
    }
    return digestRecords(records);
  }

  static String digestRecords(Map<String, String> records) {
    if (records.isEmpty) {
      throw StateError('Canonical ecosystem cannot be empty.');
    }
    final lines = <String>[];
    for (final path in records.keys.toList()..sort()) {
      final digest = records[path]!;
      if (path.isEmpty ||
          path.contains('\\') ||
          path.contains('\n') ||
          path.contains('\r') ||
          p.posix.isAbsolute(path) ||
          p.posix.normalize(path) != path ||
          path.split('/').any((segment) => segment == '.' || segment == '..')) {
        throw StateError('Canonical ecosystem contains an unsafe path.');
      }
      if (!RegExp(r'^[0-9a-f]{64}$').hasMatch(digest)) {
        throw StateError('Canonical ecosystem contains an invalid digest.');
      }
      lines.add('$digest  ./$path');
    }
    return sha256.convert(utf8.encode('${lines.join('\n')}\n')).toString();
  }

  static Map<String, String> topLevelModDigests(Directory root) {
    final result = <String, String>{};
    for (final entity in root.listSync(followLinks: false)) {
      if (entity is! File || p.extension(entity.path) != '.topiaforgemod') {
        continue;
      }
      final name = p.basename(entity.path);
      final bytes = readBoundedRegularFileSync(
        entity,
        maxBytes: CliFileLimits.package,
        allowEmpty: true,
      );
      result[name] = sha256.convert(bytes).toString();
    }
    if (result.isEmpty) {
      throw StateError('Canonical ecosystem has no standalone mod assets.');
    }
    return result;
  }
}
