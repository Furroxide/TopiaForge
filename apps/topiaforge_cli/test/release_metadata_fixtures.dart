part of 'release_metadata_test.dart';

void _writeCandidateAssets(
  Directory output,
  TopiaForgeReleaseCatalogEntry release,
) {
  for (final entry in release.mods.entries) {
    File(p.join(output.path, '${entry.key}-${entry.value}.topiaforgemod'))
      ..parent.createSync(recursive: true)
      ..writeAsStringSync('package:${entry.key}:${entry.value}\n');
  }
  _writePlatformArchive(
    File(p.join(output.path, 'TopiaForge-windows-x64.zip')),
    release,
    prefix: 'TopiaForge/',
  );
  File(
    p.join(output.path, 'topiaforge-update-v1.json'),
  ).writeAsStringSync('{"fixture":true}\n');
  File(
    p.join(output.path, 'topiaforge-update-v1.json.sig'),
  ).writeAsStringSync('{"fixture":true}\n');
  _writeJson(
    File(
      p.join(
        output.path,
        TopiaForgeReleaseMetadataBuilder.trustEvidenceFileName,
      ),
    ),
    {
      'windows-x64': {'status': 'trusted', 'exceptionApplied': false},
    },
  );
}

void _writePlatformArchive(
  File file,
  TopiaForgeReleaseCatalogEntry release, {
  required String prefix,
  String? changedPath,
}) {
  final archive = Archive();
  for (final entry in release.mods.entries) {
    final relative = 'dist/${entry.key}-${entry.value}.topiaforgemod';
    final bytes = File(
      p.join(file.parent.path, p.basename(relative)),
    ).readAsBytesSync();
    archive.addFile(ArchiveFile.bytes('$prefix$relative', bytes));
  }
  final indexBytes = utf8.encode(
    changedPath == 'dist/vpm/index.json'
        ? '{"changed":true}\n'
        : '{"packages":{}}\n',
  );
  archive.addFile(
    ArchiveFile.bytes('${prefix}dist/vpm/index.json', indexBytes),
  );
  for (final entry in release.vpmPackages.entries) {
    final relative = 'dist/vpm/${entry.key}-${entry.value}.zip';
    archive.addFile(
      ArchiveFile.string(
        '$prefix$relative',
        changedPath == relative
            ? 'changed\n'
            : 'vpm:${entry.key}:${entry.value}\n',
      ),
    );
  }
  file.writeAsBytesSync(ZipEncoder().encode(archive));
}

void _refreshChecksum(Directory directory, String name) {
  final sums = File(p.join(directory.path, 'SHA256SUMS'));
  final file = File(p.join(directory.path, name));
  final hash = sha256.convert(file.readAsBytesSync()).toString();
  final lines = sums.readAsLinesSync();
  final replacement = '$hash  $name';
  sums.writeAsStringSync(
    '${lines.map((line) => line.endsWith('  $name') ? replacement : line).join('\n')}\n',
  );
}

void _appendChecksum(Directory directory, String name) {
  final file = File(p.join(directory.path, name));
  final hash = sha256.convert(file.readAsBytesSync()).toString();
  File(
    p.join(directory.path, 'SHA256SUMS'),
  ).writeAsStringSync('$hash  $name\n', mode: FileMode.append, flush: true);
}

Map<String, Object?> _json(File file) =>
    (jsonDecode(file.readAsStringSync()) as Map).cast<String, Object?>();

void _writeJson(File file, Object value) => file.writeAsStringSync(
  '${const JsonEncoder.withIndent('  ').convert(value)}\n',
);

String _repositoryRoot() {
  var directory = Directory.current.absolute;
  while (!File(p.join(directory.path, 'TopiaForge.slnx')).existsSync()) {
    if (directory.parent.path == directory.path) {
      throw StateError('Repository root not found.');
    }
    directory = directory.parent;
  }
  return directory.path;
}
