import 'package:launcher_domain/launcher_domain.dart';

/// Strict, side-effect-free parsing shared by every process launch command.
final class CliLaunchOptions {
  const CliLaunchOptions({
    this.gameDirectory,
    this.profileId,
    this.selectionOverride,
    this.waitForAcknowledgement = true,
    this.waitTimeout = const Duration(seconds: 30),
  });
  final String? gameDirectory;
  final String? profileId;
  final LaunchSelection? selectionOverride;
  final bool waitForAcknowledgement;
  final Duration waitTimeout;

  static const valueOptions = {
    '--game-dir',
    '--profile',
    '--target',
    '--world',
    '--transition',
    '--wait-seconds',
  };
  static const switches = {'--main-menu', '--no-wait'};

  factory CliLaunchOptions.parse(
    List<String> args, {
    Set<String> extraValues = const {},
    Set<String> extraSwitches = const {},
  }) {
    final values = <String, String>{};
    final flags = <String>{};
    for (var index = 0; index < args.length; index++) {
      final flag = args[index];
      if (flag == '--gamemode') {
        throw const FormatException(
          '--gamemode is retired. Use --target <declared launch target> or --main-menu.',
        );
      }
      if (values.containsKey(flag) || flags.contains(flag)) {
        throw FormatException('Invalid duplicate option: $flag.');
      }
      if (valueOptions.contains(flag) || extraValues.contains(flag)) {
        if (++index >= args.length ||
            args[index].startsWith('--') ||
            args[index].trim().isEmpty) {
          throw FormatException('$flag requires a value.');
        }
        values[flag] = args[index];
      } else if (switches.contains(flag) || extraSwitches.contains(flag)) {
        flags.add(flag);
      } else {
        throw FormatException('Unknown launch option: $flag.');
      }
    }
    final target = values['--target'];
    if (flags.contains('--main-menu') &&
        (target != null ||
            values.containsKey('--world') ||
            values.containsKey('--transition'))) {
      throw const FormatException(
        '--main-menu cannot be combined with --target, --world or --transition.',
      );
    }
    if (target == null &&
        (values.containsKey('--world') || values.containsKey('--transition'))) {
      throw const FormatException(
        '--world or --transition requires an explicit --target.',
      );
    }
    final seconds = int.tryParse(values['--wait-seconds'] ?? '30');
    if (seconds == null || seconds < 1 || seconds > 300) {
      throw const FormatException(
        'Invalid --wait-seconds: use an integer from 1 through 300.',
      );
    }
    if (flags.contains('--no-wait') && values.containsKey('--wait-seconds')) {
      throw const FormatException(
        '--no-wait cannot be combined with --wait-seconds.',
      );
    }
    LaunchSelection? selection;
    if (flags.contains('--main-menu')) {
      selection = const LaunchSelection.mainMenu();
    }
    if (target != null) {
      try {
        selection = LaunchSelection.target(
          LaunchRequest(
            targetId: target,
            worldOverride: values['--world'],
            transitionOverride: values['--transition'],
          ),
        );
      } on FormatException catch (error) {
        throw FormatException('Invalid launch selection: ${error.message}');
      }
    }
    return CliLaunchOptions(
      gameDirectory: values['--game-dir'],
      profileId: values['--profile'],
      selectionOverride: selection,
      waitForAcknowledgement: !flags.contains('--no-wait'),
      waitTimeout: Duration(seconds: seconds),
    );
  }
}
