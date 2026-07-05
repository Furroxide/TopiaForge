part of 'robotopia_cli_test.dart';

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
