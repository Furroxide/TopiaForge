import 'dart:convert';
import 'dart:io';

import 'package:archive/archive.dart';
import 'package:crypto/crypto.dart';
import 'package:path/path.dart' as p;
import 'package:test/test.dart';

part 'topiaforge_cli_test_harness.dart';
part 'topiaforge_cli_acceptance_cases.dart';
part 'topiaforge_cli_creator_content_cases.dart';
part 'topiaforge_cli_core_cases.dart';
part 'topiaforge_cli_dev_cases.dart';
part 'topiaforge_cli_ugc_world_cases.dart';
part 'topiaforge_cli_world_contract_cases.dart';
part 'topiaforge_cli_registry_cases.dart';
part 'topiaforge_cli_scaffold_cases.dart';
part 'topiaforge_cli_multiplayer_cases.dart';

void main() {
  late _CliTestHarness harness;

  setUp(() {
    harness = _CliTestHarness();
  });

  tearDown(() {
    harness.dispose();
  });

  _coreCliTests(() => harness);
  _creatorContentCliTests(() => harness);
  _acceptanceCliTests(() => harness);
  _devCliTests(() => harness);
  _ugcAndWorldCliTests(() => harness);
  _worldContractCliTests(() => harness);
  _registryCliTests(() => harness);
  _scaffoldCliTests(() => harness);
  _multiplayerCliTests(() => harness);
}
