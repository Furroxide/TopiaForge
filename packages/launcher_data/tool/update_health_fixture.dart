import 'dart:convert';
import 'dart:io';

void main(List<String> arguments) {
  final nonce = _option(arguments, '--topiaforge-update-health-nonce');
  final path = _option(arguments, '--topiaforge-update-health-file');
  if (nonce == null && path == null) return;
  if (nonce == null ||
      path == null ||
      !RegExp(r'^[0-9a-f]{64}$').hasMatch(nonce)) {
    stderr.writeln('Invalid update health arguments.');
    exitCode = 2;
    return;
  }
  File(path).writeAsStringSync(
    '${jsonEncode({'formatVersion': 1, 'nonce': nonce, 'healthy': true, 'processId': pid, 'reportedAtUtc': DateTime.now().toUtc().toIso8601String()})}\n',
    flush: true,
  );
}

String? _option(List<String> arguments, String name) {
  final index = arguments.indexOf(name);
  if (index < 0 || index + 1 >= arguments.length) return null;
  return arguments[index + 1];
}
