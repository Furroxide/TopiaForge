import 'dart:convert';
import 'dart:io';

import 'package:archive/archive.dart';
import 'package:crypto/crypto.dart';
import 'package:path/path.dart' as p;
import 'package:test/test.dart';

part 'robotopia_cli_test_harness.dart';
part 'robotopia_cli_core_cases.dart';
part 'robotopia_cli_ugc_world_cases.dart';
part 'robotopia_cli_registry_cases.dart';

void main() {
  late _CliTestHarness harness;

  setUp(() {
    harness = _CliTestHarness();
  });

  tearDown(() {
    harness.dispose();
  });

  _coreCliTests(() => harness);
  _ugcAndWorldCliTests(() => harness);
  _registryCliTests(() => harness);
}
