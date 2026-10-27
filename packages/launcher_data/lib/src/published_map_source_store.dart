import 'dart:io';
import 'dart:typed_data';

import 'package:launcher_domain/launcher_domain.dart';
import 'package:path/path.dart' as p;
import 'atomic_file_writes.dart';
import 'atomic_files.dart';

/// Local source references only. Map bytes belong to the runtime's shared cache.
final class PublishedMapSourceStore {
  PublishedMapSourceStore(String root) : _file = File(p.join(root, fileName));
  static const fileName = 'published-map-sources-v1.json';
  static final _active = <String>{};
  final File _file;

  Future<List<PublishedMapSource>> read() => _exclusive(_read);
  Future<List<PublishedMapSource>> save(PublishedMapSource source) =>
      _exclusive(() async {
        final previous = await _read();
        final next = [...previous.where((s) => s.key != source.key), source]
          ..sort((a, b) => a.key.compareTo(b.key));
        await _write(next);
        return List.unmodifiable(next);
      });
  Future<List<PublishedMapSource>> remove(String map, String scene) =>
      _exclusive(() async {
        final key = PublishedMapSource(map: map, scene: scene, name: 'Map').key;
        final next = (await _read()).where((s) => s.key != key).toList();
        await _write(next);
        return List.unmodifiable(next);
      });

  Future<List<PublishedMapSource>> _read() async {
    ordinaryPath(_file.path);
    if (await recoverAtomicBackupIfMissing(_file, label: 'Saved maps') ==
        AtomicFileState.missing) {
      return const [];
    }
    ordinaryPath(_file.path);
    final handle = await _file.open();
    try {
      if (await handle.length() > PublishedMapSources.maximumBytes) {
        throw const FormatException('The saved map list exceeds 1 MiB.');
      }
      final bytes = BytesBuilder(copy: false);
      while (true) {
        final chunk = await handle.read(16384);
        if (chunk.isEmpty) break;
        if (bytes.length + chunk.length > PublishedMapSources.maximumBytes) {
          throw const FormatException('The saved map list exceeds 1 MiB.');
        }
        bytes.add(chunk);
      }
      return PublishedMapSources.read(bytes.takeBytes());
    } finally {
      await handle.close();
    }
  }

  Future<void> _write(List<PublishedMapSource> sources) async {
    final bytes = PublishedMapSources.write(sources);
    ordinaryPath(_file.path);
    await writeFileBytesAtomic(_file, bytes);
  }

  Future<T> _exclusive<T>(Future<T> Function() action) async {
    final key = p.canonicalize(_file.path);
    if (!_active.add(key)) {
      throw StateError(
        'Saved maps are being updated. Try again when the other operation finishes.',
      );
    }
    RandomAccessFile? handle;
    try {
      ordinaryPath(_file.path);
      await _file.parent.create(recursive: true);
      final lock = File('${_file.path}.lock');
      ordinaryPath(lock.path);
      handle = await lock.open(mode: FileMode.append);
      await handle.lock();
      ordinaryPath(_file.path);
      return await action();
    } on FileSystemException {
      throw StateError(
        'Saved maps could not be read or updated. Close other source editors and check folder access, then try again.',
      );
    } finally {
      try {
        await handle?.close();
      } finally {
        _active.remove(key);
      }
    }
  }

  /// Reject a linked leaf or ancestor before reading, creating or replacing it.
  static void ordinaryPath(String path) {
    for (var current = p.absolute(path); ;) {
      if (FileSystemEntity.typeSync(current, followLinks: false) ==
          FileSystemEntityType.link) {
        throw StateError(
          'Saved maps require ordinary folders and files without links.',
        );
      }
      final parent = p.dirname(current);
      if (parent == current) return;
      current = parent;
    }
  }
}
