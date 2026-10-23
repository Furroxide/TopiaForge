/// Makes the multiplayer settings file unreadable for the code a test runs,
/// the way another program holding it does, on any platform and for any
/// account, root included: inside [whileSettingsUnreadable], reading
/// `settings.json` fails while every other file behaves as it is.
library;

import 'dart:io';
import 'dart:typed_data';

import 'package:launcher_data/launcher_data.dart';

/// Runs [body] where `settings.json` cannot be read. Anything [body] starts
/// keeps the condition until it finishes, as its futures run in the zone.
Future<T> whileSettingsUnreadable<T>(Future<T> Function() body) =>
    IOOverrides.runZoned(
      body,
      createFile: (path) => path.endsWith(MultiplayerStore.settingsFileName)
          ? _UnreadableFile(path)
          : _realFile(path),
      // Inside an override zone the SDK's own asynchronous type lookup reads
      // every path as missing; its synchronous one does not.
      fseGetType: (path, followLinks) async =>
          FileSystemEntity.typeSync(path, followLinks: followLinks),
    );

/// A file another program holds open for itself: it is there, and every
/// read fails.
final class _UnreadableFile implements File {
  _UnreadableFile(this.path);

  @override
  final String path;

  FileSystemException get _denied => FileSystemException(
    'The process cannot access the file because it is being used by another '
    'process',
    path,
    const OSError('Access is denied', 32),
  );

  @override
  Future<int> length() async => throw _denied;

  @override
  Future<Uint8List> readAsBytes() async => throw _denied;

  @override
  dynamic noSuchMethod(Invocation invocation) => super.noSuchMethod(invocation);
}

final class _Plain extends IOOverrides {}

final _plain = _Plain();

/// A file as `dart:io` makes one, whatever overrides are in force.
File _realFile(String path) =>
    IOOverrides.runWithIOOverrides(() => File(path), _plain);
