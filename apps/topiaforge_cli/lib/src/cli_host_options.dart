import 'package:launcher_domain/launcher_domain.dart';

import 'cli_launch_options.dart';

final class CliHostOptions {
  CliHostOptions(List<String> args)
    : launch = CliLaunchOptions.parse(
        args,
        extraValues: const {
          '--name',
          '--port',
          '--max-players',
          '--visibility',
        },
        extraSwitches: const {
          '--yes',
          '--allow-direct',
          '--serve-packages',
          '--password',
        },
      ),
      yes = args.contains('--yes'),
      allowDirect = args.contains('--allow-direct'),
      servePackages = args.contains('--serve-packages'),
      password = args.contains('--password') {
    String? value(String flag) {
      final index = args.indexOf(flag);
      return index < 0 ? null : args[index + 1];
    }

    if (!launch.waitForAcknowledgement) {
      throw const FormatException(
        'Hosting needs runtime confirmation; omit --no-wait.',
      );
    }
    // The game asks for a password only once it reaches its menu, and the
    // player types it there, so a protected session waits as long as the
    // command allows unless --wait-seconds says otherwise.
    waitTimeout = password && !args.contains('--wait-seconds')
        ? const Duration(seconds: 300)
        : launch.waitTimeout;
    name = value('--name');
    port = _number(value('--port'), 0, 65535);
    maxPlayers = _number(value('--max-players'), 2, 16);
    if (port == LanBeacon.discoveryPort) {
      throw const FormatException(
        'Choose a hosting port other than the LAN discovery port.',
      );
    }
    final mode = value('--visibility');
    visibility = mode == null
        ? null
        : ServerVisibility.values.where((v) => v.wireName == mode).firstOrNull;
    if (mode != null && visibility == null) {
      throw const FormatException(
        'Use --visibility lan|private|invite|public.',
      );
    }
    if (name != null) {
      try {
        LaunchHostOptions(name: name!);
      } on Object {
        throw const FormatException(
          'Use a server name of 1 to 48 printable characters.',
        );
      }
    }
  }

  final CliLaunchOptions launch;
  final bool yes, allowDirect, servePackages, password;

  /// How long to wait for the game's hosting confirmation.
  late final Duration waitTimeout;
  late final String? name;
  late final int? port, maxPlayers;
  late final ServerVisibility? visibility;

  static int? _number(String? text, int min, int max) {
    if (text == null) return null;
    final value = int.tryParse(text);
    if (value == null || value < min || value > max) {
      throw FormatException('Use a whole number from $min through $max.');
    }
    return value;
  }
}
