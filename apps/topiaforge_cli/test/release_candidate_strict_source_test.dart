import 'dart:io';

import 'package:path/path.dart' as p;
import 'package:test/test.dart';

import 'release_candidate_fixture.dart';

void main() {
  test(
    'qualification rejects duplicate frozen platform toolchain fields',
    () async {
      Future<void> qualifyDuplicateSource() async {
        final fixture = await CandidateFixture.create(
          mutateContracts: (root) {
            final file = File(
              p.join(root.path, 'release/platform-toolchains.json'),
            );
            file.writeAsStringSync(
              file.readAsStringSync().replaceFirst(
                '{',
                '{"windows":{"msvc":"untrusted","windowsSdk":"untrusted"},',
              ),
            );
          },
        );
        try {
          await fixture.qualify();
        } finally {
          fixture.dispose();
        }
      }

      await expectLater(qualifyDuplicateSource(), throwsStateError);
    },
  );
}
