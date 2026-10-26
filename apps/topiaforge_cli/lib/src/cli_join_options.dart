import 'package:launcher_domain/launcher_domain.dart';

final class CliJoinOptions {
  CliJoinOptions(List<String> args) {
    if (args.isEmpty || args.first.startsWith('--')) {
      throw const FormatException('A join target is required.');
    }
    target = parseTarget(args.first);
    final seen = <String>{};
    for (var i = 1; i < args.length; i++) {
      final flag = args[i];
      if (!seen.add(flag)) throw const FormatException('Repeated option.');
      switch (flag) {
        case '--yes':
          yes = true;
        case '--allow-direct':
          allowDirect = true;
        case '--allow-host-packages':
          allowHostPackages = true;
        case '--game-dir':
        case '--wait-seconds':
          if (++i == args.length || args[i].startsWith('--')) {
            throw const FormatException('Missing value.');
          }
          if (flag == '--game-dir') {
            if (args[i].trim().isEmpty) {
              throw const FormatException('Empty path.');
            }
            gameDirectory = args[i];
          } else {
            final seconds = int.tryParse(args[i]);
            if (seconds == null || seconds < 1 || seconds > 300) {
              throw const FormatException('Wait is 1 through 300 seconds.');
            }
            waitTimeout = Duration(seconds: seconds);
          }
        default:
          throw const FormatException('Unknown option.');
      }
    }
  }

  late final JoinTarget target;
  String? gameDirectory;
  bool yes = false, allowDirect = false, allowHostPackages = false;
  // Includes the game asking for a password, which is entered only there.
  Duration waitTimeout = const Duration(seconds: 300);

  static JoinTarget parseTarget(String text) {
    final link = SessionLink.tryParse(text);
    if (link != null) return JoinLinkTarget(link);
    final code = InviteCode.tryNormalize(text);
    if (code != null) return JoinLinkTarget(SessionLink.invite(code));
    final address = JoinAddressTarget.tryParse(text);
    if (address != null) return address;
    throw const FormatException('Invalid join target.');
  }
}
