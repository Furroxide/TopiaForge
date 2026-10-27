part of '../local_developer_repository.dart';

extension _MapPackageContent on LocalDeveloperRepository {
  Map<String, String> _mapFileHashes(
    Directory root,
    ModManifest manifest,
    Map<String, String> packed,
    void Function(String, File) addFile,
  ) {
    final hashes = <String, String>{};
    for (final world
        in manifest.contributions?.worlds ?? const <ModWorldDeclaration>[]) {
      final content = world.content;
      if (content?.kind != ModWorldContent.mapKind || content!.file.isEmpty) {
        continue;
      }
      final path = content.file;
      if (!packed.containsKey(path)) {
        // Compiled mods normally stage only conventional content directories.
        // An explicit map may live elsewhere in the project, but never behind
        // a link or outside its ordinary relative ancestry.
        var parent = root.path;
        final segments = path.split('/');
        for (final segment in segments.take(segments.length - 1)) {
          parent = p.join(parent, segment);
          if (FileSystemEntity.typeSync(parent, followLinks: false) !=
              FileSystemEntityType.directory) {
            throw StateError(
              'Map file was not included in the package: $path (parent is not an ordinary directory).',
            );
          }
        }
        final source = File(p.join(root.path, path));
        if (FileSystemEntity.typeSync(source.path, followLinks: false) !=
            FileSystemEntityType.file) {
          throw StateError('Map file was not included in the package: $path');
        }
        addFile(path, source);
      }
      hashes[path] = packed[path]!;
    }
    return hashes;
  }
}
