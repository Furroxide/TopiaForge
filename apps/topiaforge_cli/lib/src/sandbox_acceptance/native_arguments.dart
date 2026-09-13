import 'package:path/path.dart' as p;
import 'native_options.dart';

const sandboxNativeUsage =
    'Usage: topiaforge acceptance sandbox run '
    '--source-root PATH --isolation-record PATH --device-profile PATH '
    '--packages PATH --broker PATH --driver-manifest PATH --spec PATH '
    '--output-root PATH [--source-workspace PATH] [--timeout-seconds 30..14400]\n'
    '       topiaforge acceptance sandbox verify --annex PATH '
    '--isolation-record PATH --device-profile PATH --packages PATH '
    '--broker PATH --driver-manifest PATH --spec PATH';

final class SandboxNativeCommand {
  const SandboxNativeCommand(this.command, this.paths);
  final String command;
  final SandboxNativePaths paths;
  static SandboxNativeCommand parse(List<String> args) {
    if (args.isEmpty || !['run', 'verify'].contains(args.first)) {
      throw FormatException(sandboxNativeUsage);
    }
    final command = args.first;
    final required = <String>{
      '--isolation-record',
      '--device-profile',
      '--packages',
      '--broker',
      '--driver-manifest',
      '--spec',
      if (command == 'run') ...[
        '--source-root',
        '--output-root',
      ] else
        '--annex',
    };
    final allowed = {
      ...required,
      if (command == 'run') ...['--source-workspace', '--timeout-seconds'],
    };
    final values = <String, String>{};
    for (var index = 1; index < args.length; index += 2) {
      final key = args[index];
      if (!allowed.contains(key) ||
          values.containsKey(key) ||
          index + 1 >= args.length ||
          args[index + 1].startsWith('--') ||
          args[index + 1].trim().isEmpty) {
        throw FormatException(sandboxNativeUsage);
      }
      final value = args[index + 1];
      if (key != '--timeout-seconds' &&
          (!p.isAbsolute(value) || p.normalize(value) != value)) {
        throw const FormatException(
          'Native paths must be explicit canonical absolute paths.',
        );
      }
      values[key] = value;
    }
    if (!required.every(values.containsKey)) {
      throw FormatException(sandboxNativeUsage);
    }
    final seconds = int.tryParse(values['--timeout-seconds'] ?? '1800');
    if (seconds == null || seconds < 30 || seconds > 14400) {
      throw const FormatException(
        'Native timeout must be 30 through 14400 seconds.',
      );
    }
    return SandboxNativeCommand(
      command,
      SandboxNativePaths(
        repositoryRoot: values['--source-root'] ?? '',
        isolationRecordPath: values['--isolation-record']!,
        deviceProfilePath: values['--device-profile']!,
        packagesPath: values['--packages']!,
        brokerPath: values['--broker']!,
        driverManifestPath: values['--driver-manifest']!,
        specPath: values['--spec']!,
        outputRoot: values['--output-root'] ?? '',
        annexPath: values['--annex'] ?? '',
        sourceWorkspacePath: values['--source-workspace'] ?? '',
        timeout: Duration(seconds: seconds),
      ),
    );
  }
}
