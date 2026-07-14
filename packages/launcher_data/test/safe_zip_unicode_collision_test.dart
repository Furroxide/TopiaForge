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
