part of '../local_developer_repository.dart';

extension LocalDeveloperTemplateCopy on LocalDeveloperRepository {
  void _copyDirectory(
    Directory source,
    Directory destination, {
    bool excludeUnityGenerated = false,
  }) {
    if (FileSystemEntity.typeSync(source.path, followLinks: false) !=
        FileSystemEntityType.directory) {
      throw StateError(
        'Template source is not a regular directory: ${source.path}',
      );
    }
    final destinationType = FileSystemEntity.typeSync(
      destination.path,
      followLinks: false,
    );
    if (destinationType != FileSystemEntityType.notFound &&
        destinationType != FileSystemEntityType.directory) {
      throw StateError(
        'Template destination is not a regular directory: ${destination.path}',
      );
    }
    destination.parent.createSync(recursive: true);
    final nonce = '$pid-${DateTime.now().microsecondsSinceEpoch}';
    final staging = Directory('${destination.path}.topiaforge-new-$nonce');
    final backup = Directory('${destination.path}.topiaforge-backup-$nonce');
    final swap = _StagedDeveloperDirectorySwap(
      target: destination,
      backup: backup,
      staging: staging,
    );
    try {
      staging.createSync();
      _copyTemplateTree(
        source,
        staging,
        excludeUnityGenerated: excludeUnityGenerated,
      );
      swap.commit();
      if (backup.existsSync()) {
        backup.deleteSync(recursive: true);
      }
    } on Object {
      swap.rollback();
      if (staging.existsSync()) {
        staging.deleteSync(recursive: true);
      }
      rethrow;
    }
  }

  void _copyTemplateTree(
    Directory source,
    Directory destination, {
    required bool excludeUnityGenerated,
  }) {
    var entryCount = 0;
    var byteCount = 0;

    void visit(Directory current) {
      if (FileSystemEntity.typeSync(current.path, followLinks: false) !=
          FileSystemEntityType.directory) {
        throw StateError('Template directory changed while being copied.');
      }
      final entities = current.listSync(followLinks: false)
        ..sort((left, right) => left.path.compareTo(right.path));
      for (final entity in entities) {
        final relative = p.relative(entity.path, from: source.path);
        if (excludeUnityGenerated && _isIgnoredUnityTemplatePath(relative)) {
          continue;
        }
        entryCount++;
        if (entryCount > _maxTemplateCopyEntries) {
          throw StateError('Template exceeds the 8192-entry limit.');
        }
        final type = FileSystemEntity.typeSync(entity.path, followLinks: false);
        final target = p.join(destination.path, relative);
        if (type == FileSystemEntityType.directory) {
          Directory(target).createSync(recursive: true);
          visit(Directory(entity.path));
        } else if (type == FileSystemEntityType.file) {
          final bytes = _readDeveloperFileBoundedSync(
            File(entity.path),
            maxBytes: _maxTemplateCopyFileBytes,
            label: 'Template file $relative',
          );
          if (byteCount > _maxTemplateCopyBytes - bytes.length) {
            throw StateError('Template exceeds the 2 GB expanded-size limit.');
          }
          byteCount += bytes.length;
          final output = File(target)..createSync(recursive: true);
          output.writeAsBytesSync(bytes, flush: true);
        } else {
          throw StateError(
            'Template contains a symlink or special entry: $relative',
          );
        }
      }
    }

    visit(source);
  }
}

bool _isIgnoredUnityTemplatePath(String relative) {
  final segments = p.split(relative);
  final directories = segments
      .take(segments.length - 1)
      .map((segment) => segment.toLowerCase());
  if (directories.any(_unityGeneratedDirectories.contains)) {
    return true;
  }
  final basename = segments.last.toLowerCase();
  if (_unityGeneratedDirectories.contains(basename)) {
    return true;
  }
  return _unityGeneratedFileNames.contains(basename) ||
      _unityGeneratedFileExtensions.contains(p.extension(basename));
}

const _unityGeneratedDirectories = {
  '.git',
  '.idea',
  '.vs',
  'bin',
  'build',
  'builds',
  'library',
  'logs',
  'memorycaptures',
  'obj',
  'recordings',
  'temp',
  'usersettings',
};

const _unityGeneratedFileNames = {'.ds_store', 'sysinfo.txt'};
const _unityGeneratedFileExtensions = {'.csproj', '.sln', '.suo', '.user'};
