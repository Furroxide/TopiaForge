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
  final file = File(p.join(temp.path, fileName));
  file.writeAsBytesSync(ZipEncoder().encode(archive));
  return file;
}

class _CliTestHarness {
  _CliTestHarness()
    : temp = Directory.systemTemp.createTempSync('robotopia-cli-test-');

  final Directory temp;

  Future<ProcessResult> runCli(List<String> args) {
    return Process.run(
      Platform.resolvedExecutable,
      ['run', 'robotopia', ...args],
      workingDirectory: Directory.current.path,
      environment: {
        ...Platform.environment,
        'ROBOTOPIA_DATA_ROOT': p.join(temp.path, 'data'),
      },
    );
  }

  void dispose() {
    if (temp.existsSync()) {
      temp.deleteSync(recursive: true);
    }
  }
}
