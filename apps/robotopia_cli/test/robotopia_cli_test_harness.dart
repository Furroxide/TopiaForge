part of 'robotopia_cli_test.dart';

/// Forges a syntactically valid .robotopiamod zip without needing dotnet.
File _writeTestPackage(
  Directory temp, {
  required Map<String, Object?> manifest,
  String fileName = 'package.robotopiamod',
}) {
  final archive = Archive()
    ..addFile(ArchiveFile.string('robotopia.mod.json', jsonEncode(manifest)))
    ..addFile(ArchiveFile.string('Mod.dll', 'dll-bytes'));
  final licenseFiles = manifest['licenseFiles'];
  if (licenseFiles is List) {
    for (final path in licenseFiles.whereType<String>()) {
      archive.addFile(ArchiveFile.string(path, 'Test fixture license.'));
    }
  }
  final file = File(p.join(temp.path, fileName));
  file.writeAsBytesSync(ZipEncoder().encode(archive));
  return file;
}

class _CliTestHarness {
  _CliTestHarness()
    : temp = Directory.systemTemp.createTempSync('robotopia-cli-test-');

  final Directory temp;

  Future<ProcessResult> runCli(
    List<String> args, {
    Map<String, String> environment = const {},
    String? workingDirectory,
  }) {
    final packageRoot = Directory.current.absolute.path;
    return Process.run(
      Platform.resolvedExecutable,
      workingDirectory == null
          ? ['run', 'robotopia', ...args]
          : [p.join(packageRoot, 'bin', 'robotopia.dart'), ...args],
      workingDirectory: workingDirectory ?? packageRoot,
      environment: {
        ...Platform.environment,
        'ROBOTOPIA_DATA_ROOT': p.join(temp.path, 'data'),
        ...environment,
      },
    );
  }

  void dispose() {
    if (temp.existsSync()) {
      temp.deleteSync(recursive: true);
    }
  }
}
