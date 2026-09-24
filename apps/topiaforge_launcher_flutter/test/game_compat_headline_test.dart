import 'package:flutter_test/flutter_test.dart';
import 'package:launcher_domain/launcher_domain.dart';
import 'package:topiaforge_launcher_flutter/src/screens.dart';

void main() {
  test('a passing check claims only that declared game APIs resolve', () {
    final headline = gameCompatHeadline(
      const GameCompatStatus(status: 'ok', gameVersionLabel: 'build 2478'),
    );

    expect(headline, contains('No declared game API changed'));
    expect(headline, contains('(build 2478)'));
    // Passing the binding check does not mean every mod supports the build.
    expect(headline, contains('supported Robotopia builds'));
    expect(headline, isNot(contains('All mod features are compatible')));
  });

  test('a passing check falls back to the canonical game version', () {
    expect(
      gameCompatHeadline(
        const GameCompatStatus(status: 'ok', gameVersion: '0.0.2478'),
      ),
      contains('(0.0.2478)'),
    );
  });

  test('other outcomes keep their guidance', () {
    expect(
      gameCompatHeadline(const GameCompatStatus(status: 'broken')),
      contains('rely on game APIs that changed'),
    );
    expect(
      gameCompatHeadline(GameCompatStatus.skipped()),
      'No game installation was detected to check.',
    );
    expect(
      gameCompatHeadline(const GameCompatStatus(status: 'unavailable')),
      contains('could not be verified'),
    );
  });
}
