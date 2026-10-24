import 'dart:io';

import 'package:archive/archive.dart';
import 'package:launcher_data/launcher_data.dart';
import 'package:path/path.dart' as p;
import 'package:test/test.dart';

import 'zip_entry_support.dart';

/// An entry whose data expands beyond the size it declares is refused
/// against that size, whether it is read or extracted; one whose data is
/// damaged is refused as damaged, whatever size it declares. Archive 4.5
/// and later enforce the declared size in their decoder, and older versions
/// leave it to the reader's own length check: both refusals name the size.
void main() {
  late Directory root;

  setUp(() => root = Directory.systemTemp.createTempSync('safe-zip-decode-'));
  tearDown(() => root.deleteSync(recursive: true));

  test('extracting refuses an entry that expands beyond its declared size, and '
      'leaves nothing behind', () {
    final zip = ZipEncoder().encode(_archive());
    declareEntrySize(zip, 'payload.bin', 1);
    final archive = SafeZipArchive.decode(zip, label: 'Package');
    final target = Directory(p.join(root.path, 'extracted'));

    expect(
      () => archive.extractTo(target),
      throwsA(_refusal(contains('declared'), 'payload.bin')),
    );
    expect(target.existsSync(), isFalse);
  });

  test('a damaged entry is refused as damaged, read or extracted', () {
    final zip = ZipEncoder().encode(_archive());
    damageEntryData(zip, 'payload.bin');
    final damaged = _refusal(
      allOf(contains('damaged'), isNot(contains('declared'))),
      'payload.bin',
    );

    // Each way of reading from an archive of its own: before 4.5, archive
    // leaves an entry that failed to decode read to its end, so a second
    // decode of it would find no data at all rather than damaged data.
    expect(
      () => SafeZipArchive.decode(
        zip,
        label: 'Package',
      ).entryNamed('payload.bin')!.readBytes(maxBytes: 1 << 20),
      throwsA(damaged),
    );
    final target = Directory(p.join(root.path, 'extracted'));
    expect(
      () => SafeZipArchive.decode(zip, label: 'Package').extractTo(target),
      throwsA(damaged),
    );
    expect(target.existsSync(), isFalse);
  });
}

Archive _archive() => Archive()
  ..addFile(ArchiveFile.string('readme.txt', 'kept'))
  ..addFile(ArchiveFile.string('payload.bin', 'payload ' * 64));

Matcher _refusal(Matcher words, String entry) => isA<StateError>().having(
  (error) => error.message,
  'message',
  allOf(words, contains(entry)),
);
