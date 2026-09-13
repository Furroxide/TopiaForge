import 'dart:convert';
import 'dart:io';

import 'package:topiaforge/src/sandbox_acceptance/sandbox_specification.dart';
import 'package:topiaforge/src/sandbox_acceptance/sandbox_verifier.dart';

/// Developer-only offline verifier. No game launch, package install or gate write.
void main(List<String> args) {
  if (args.length != 2) {
    stderr.writeln(
      'Usage: dart run tool/verify_sandbox_observations.dart '
      '<specification.json> <offline-observations.json>',
    );
    exitCode = 64;
    return;
  }
  try {
    final spec = SandboxSpecification.parse(_read(args[0]));
    final result = SandboxObservationVerifier().verify(spec, _read(args[1]));
    stdout.writeln(const JsonEncoder.withIndent('  ').convert(result.toJson()));
    // Zero means only that the supplied offline measurements meet the contract.
    exitCode = result.allOfflineChecksPassed ? 0 : 1;
  } on Object catch (error) {
    stderr.writeln('Sandbox offline verification refused: $error');
    exitCode = 2;
  }
}

List<int> _read(String path) {
  if (FileSystemEntity.typeSync(path, followLinks: false) !=
      FileSystemEntityType.file) {
    throw StateError('Inputs must be ordinary files.');
  }
  final handle = File(path).openSync();
  try {
    // Read a bounded amount from one handle, even if the named file grows.
    final bytes = handle.readSync(256 * 1024 + 1);
    if (bytes.length > 256 * 1024) throw StateError('Input is oversized.');
    return bytes;
  } finally {
    handle.closeSync();
  }
}
