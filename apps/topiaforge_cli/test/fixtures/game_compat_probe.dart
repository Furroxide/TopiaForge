import 'dart:convert';
import 'dart:io';

void main(List<String> arguments) {
  if (Platform.environment['TOPIAFORGE_GAME_COMPAT_PROBE_MODE'] == 'overflow') {
    final chunk = 'x' * (64 * 1024);
    for (var index = 0; index < 128; index += 1) {
      stdout.write(chunk);
    }
    return;
  }

  stdout.writeln(
    jsonEncode({
      'probe': 'packaged-game-compat',
      'arguments': arguments,
      'resolvedExecutable': Platform.resolvedExecutable,
    }),
  );
}
