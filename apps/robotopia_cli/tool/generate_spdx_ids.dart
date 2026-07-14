import 'dart:io';

import 'package:crypto/crypto.dart';
import 'package:robotopia/src/bounded_file_reader.dart';

const _licensesSha =
    'f728c534d8bd1044fc515a2ddb2292be99559021d830bfa3281be0bcd36302ee';
const _exceptionsSha =
    'bd145bb558f44432fcd6f0d7e956ed0124dff72af7641a7cfcb1b557dc390a5b';

Future<void> main(List<String> arguments) async {
  if (arguments.length != 3) {
    stderr.writeln(
      'Usage: dart run tool/generate_spdx_ids.dart licenses.json exceptions.json lib/src/spdx_ids_3_28.g.dart',
    );
    exitCode = 64;
    return;
  }
  final licensesFile = File(arguments[0]);
  final exceptionsFile = File(arguments[1]);
  await _requireHash(licensesFile, _licensesSha);
  await _requireHash(exceptionsFile, _exceptionsSha);
  final licenses = readBoundedJsonObjectSync(
    licensesFile,
    maxBytes: 16 * 1024 * 1024,
  );
  final exceptions = readBoundedJsonObjectSync(
    exceptionsFile,
    maxBytes: 16 * 1024 * 1024,
  );
  if (licenses['licenseListVersion'] != '3.28.0' ||
      exceptions['licenseListVersion'] != '3.28.0') {
    throw StateError('Expected SPDX license-list-data 3.28.0.');
  }
  final licenseIds = [
    for (final item in licenses['licenses'] as List)
      (item as Map)['licenseId'] as String,
  ]..sort();
  final exceptionIds = [
    for (final item in exceptions['exceptions'] as List)
      (item as Map)['licenseExceptionId'] as String,
  ]..sort();
  final output = StringBuffer()
    ..writeln('// GENERATED from SPDX license-list-data v3.28.0. Do not edit.')
    ..writeln("const spdxLicenseListVersion = '3.28.0';")
    ..writeln('const spdxLicenseIds = <String>{');
  for (final id in licenseIds) {
    output.writeln("  '${id.replaceAll("'", r"\'")}',");
  }
  output
    ..writeln('};')
    ..writeln('const spdxExceptionIds = <String>{');
  for (final id in exceptionIds) {
    output.writeln("  '${id.replaceAll("'", r"\'")}',");
  }
  output.writeln('};');
  await File(arguments[2]).writeAsString(output.toString(), flush: true);
}

Future<void> _requireHash(File file, String expected) async {
  final actual = (await sha256.bind(file.openRead()).single).toString();
  if (actual != expected) {
    throw StateError('${file.path} SHA-256 mismatch: $actual');
  }
}
