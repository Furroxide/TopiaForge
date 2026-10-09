part of 'topiaforge.dart';

extension _TopiaForgeSandboxAcceptanceCommands on _TopiaForgeCli {
  Future<int> _sandboxAcceptance(List<String> args) async {
    if (args.contains('--help')) {
      stdout.writeln(sandboxNativeUsage);
      stdout.writeln(
        'Private supplementary automation; never release qualification.',
      );
      return 0;
    }
    final SandboxNativeCommand parsed;
    try {
      parsed = SandboxNativeCommand.parse(args);
    } on FormatException catch (error) {
      throw UsageError(error.message);
    }
    if (parsed.command == 'run') {
      final result = await runSandboxNative(parsed.paths);
      stdout.writeln(jsonEncode(result));
      return result['status'] == 'passed' ? 0 : 1;
    }
    try {
      final result = await verifySandboxNativeFiles(parsed.paths);
      stdout.writeln(jsonEncode(result.toJson()));
      return result.allNativeChecksPassed ? 0 : 1;
    } on Object {
      stderr.writeln(
        'Native Sandbox verification refused; inspect private input paths and retained evidence.',
      );
      return 2;
    }
  }
}
