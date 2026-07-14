import 'dart:convert';
import 'dart:io';

import 'package:archive/archive.dart';
import 'package:launcher_data/launcher_data.dart';
import 'package:path/path.dart' as p;
import 'package:robotopia/src/release_package_builder.dart';
import 'package:robotopia/src/release_package_io.dart';
import 'package:robotopia/src/release_package_macos.dart';
import 'package:robotopia/src/release_package_models.dart';
import 'package:robotopia/src/release_package_notices.dart';
import 'package:robotopia/src/release_package_payload.dart';
import 'package:robotopia/src/release_package_validator.dart';
import 'package:robotopia/src/release_package_windows.dart';
import 'package:test/test.dart';

part 'release_package_builder_fixtures.dart';
part 'release_package_builder_macos_cases.dart';
part 'release_package_builder_packaging_cases.dart';
part 'release_package_builder_process_io_cases.dart';

late Directory temp;

void main() {
  setUp(() {
    temp = Directory.systemTemp.createTempSync('release-package-test-');
  });

  tearDown(() {
    if (temp.existsSync()) {
      temp.deleteSync(recursive: true);
    }
  });

  _registerReleaseProcessAndIoTests();
  _registerReleasePackagingTests();
  _registerReleaseMacPackagingTests();
}
