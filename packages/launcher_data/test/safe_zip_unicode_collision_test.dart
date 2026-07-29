import 'package:archive/archive.dart';
import 'package:launcher_data/launcher_data.dart';
import 'package:test/test.dart';

void main() {
  test('rejects canonically equivalent archive paths', () {
    expect(
      () => _decode(['caf\u00e9.txt', 'cafe\u0301.txt']),
      throwsA(_duplicatePath),
    );
  });

  test('rejects full case-fold collisions', () {
    expect(
      () => _decode(['Stra\u00dfe.dll', 'STRASSE.dll']),
      throwsA(_duplicatePath),
    );
    expect(
      () => _decode(['\u039f\u03a3.json', '\u03bf\u03c2.json']),
      throwsA(_duplicatePath),
    );
  });

  test('rejects compatibility-normalized collisions', () {
    expect(portableArchiveCollisionKey('\u212ait'), 'kit');
    expect(
      () => _decode(['\u212ait/readme.txt', 'kit/readme.txt']),
      throwsA(_duplicatePath),
    );
  });

  test('case-insensitive lookup uses the same collision semantics', () {
    final archive = _decode(['Stra\u00dfe.dll']);

    expect(
      archive.entryNamed('STRASSE.DLL', caseSensitive: false)?.name,
      'Stra\u00dfe.dll',
    );
  });

  test('rejects invisible path controls', () {
    expect(
      () => portableArchivePath('safe\u200bname.dll'),
      throwsA(isA<StateError>()),
    );
  });

  test('rejects escaping and nested symbolic links in update archives', () {
    final escaping = Archive()
      ..addFile(
        (ArchiveFile.string('bundle/link', '../../outside')
          ..symbolicLink = '../../outside'
          ..mode = 0xa1ff),
      );
    expect(
      () => SafeZipArchive.decode(
        ZipEncoder().encode(escaping),
        allowContainedLinks: true,
      ),
      throwsA(predicate((error) => error.toString().contains('escaping'))),
    );

    final nested = Archive()
      ..addFile(
        (ArchiveFile.string('bundle/link', 'target')
          ..symbolicLink = 'target'
          ..mode = 0xa1ff),
      )
      ..addFile(ArchiveFile.string('bundle/link/payload', 'unsafe'));
    expect(
      () => SafeZipArchive.decode(
        ZipEncoder().encode(nested),
        allowContainedLinks: true,
      ),
      throwsA(
        predicate((error) => error.toString().contains('beneath a link')),
      ),
    );
  });

  test('rejects special files and privileged permission bits', () {
    final privileged = Archive()
      ..addFile(ArchiveFile.string('payload', 'unsafe')..mode = 0x89a4);
    expect(
      () => SafeZipArchive.decode(ZipEncoder().encode(privileged)),
      throwsA(predicate((error) => error.toString().contains('setuid'))),
    );

    final device = Archive()
      ..addFile(ArchiveFile.string('device', 'unsafe')..mode = 0x21a4);
    expect(
      () => SafeZipArchive.decode(ZipEncoder().encode(device)),
      throwsA(
        predicate((error) => error.toString().contains('unsupported file')),
      ),
    );
  });
}

final _duplicatePath = predicate(
  (error) => error.toString().contains('duplicate path'),
);

SafeZipArchive _decode(List<String> names) {
  final source = Archive();
  for (final name in names) {
    source.addFile(ArchiveFile.string(name, name));
  }
  return SafeZipArchive.decode(
    ZipEncoder().encode(source),
    label: 'Unicode fixture',
  );
}
