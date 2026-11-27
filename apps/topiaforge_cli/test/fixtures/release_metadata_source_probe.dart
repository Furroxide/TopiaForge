import 'dart:io';

import 'package:topiaforge/src/release_metadata_source.dart';

Future<void> main(List<String> arguments) async {
  await verifyMetadataPublicationSource(
    arguments[0],
    arguments[1],
    '0.1.0-rc.1',
    arguments[2],
    arguments[3],
    allowUnresolved: false,
  );
  stdout.writeln('source accepted');
}
