part of '../local_launcher_repository.dart';

final _packageCacheTails = <String, Future<void>>{};
final _packageCacheLockZone = Object();

extension _PackageCacheLock on LocalLauncherRepository {
  /// Serialize receipt commits and cache retirement, including across CLI
  /// processes. Nested reads in an install share its lease. A competing
  /// process gets an actionable retry instead of racing a cache deletion.
  Future<T> _withPackageCacheLock<T>(Future<T> Function() action) async {
    final key = p.canonicalize(_dataRoot.absolute.path);
    if (Zone.current[_packageCacheLockZone] == key) return action();
    final previous = _packageCacheTails[key] ?? Future<void>.value();
    final completion = Completer<void>();
    _packageCacheTails[key] = completion.future;
    await previous;
    RandomAccessFile? handle;
    try {
      try {
        _requireCacheDirectory(_dataRoot);
        final lock = File(p.join(_dataRoot.path, 'package-cache.lock'));
        final type = FileSystemEntity.typeSync(lock.path, followLinks: false);
        if (type != FileSystemEntityType.notFound &&
            type != FileSystemEntityType.file) {
          throw StateError('Package cache lock is not a regular file.');
        }
        handle = await lock.open(mode: FileMode.append);
        await handle.lock(FileLock.exclusive);
      } on Object {
        throw StateError(
          'The package cache is unavailable or in use. Close other '
          'TopiaForge install operations, check free disk space and folder '
          'permissions, then retry.',
        );
      }
      return await runZoned(action, zoneValues: {_packageCacheLockZone: key});
    } finally {
      try {
        await handle?.close();
      } finally {
        completion.complete();
        if (identical(_packageCacheTails[key], completion.future)) {
          _packageCacheTails.remove(key);
        }
      }
    }
  }
}

void _requireCacheDirectory(Directory directory) {
  final type = FileSystemEntity.typeSync(directory.path, followLinks: false);
  if (type != FileSystemEntityType.notFound &&
      type != FileSystemEntityType.directory) {
    throw StateError('Package cache storage is not a regular directory.');
  }
  directory.createSync(recursive: true);
}
