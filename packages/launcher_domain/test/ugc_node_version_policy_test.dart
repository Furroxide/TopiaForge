import 'package:launcher_domain/launcher_domain.dart';
import 'package:test/test.dart';

void main() {
  test('accepts the supported Node floor and newer versions', () {
    expect(UgcNodeVersionPolicy.supports('v24.16.0'), isTrue);
    expect(UgcNodeVersionPolicy.supports('24.16.1\n'), isTrue);
    expect(UgcNodeVersionPolicy.supports('v26.0.0'), isTrue);
  });

  test('rejects EOL, older, prerelease, partial, and malformed versions', () {
    expect(UgcNodeVersionPolicy.supports('v20.20.2'), isFalse);
    expect(UgcNodeVersionPolicy.supports('v24.15.9'), isFalse);
    expect(UgcNodeVersionPolicy.supports('v24.16.0-nightly'), isFalse);
    expect(UgcNodeVersionPolicy.supports('v24.16.0+local'), isFalse);
    expect(UgcNodeVersionPolicy.supports('v24'), isFalse);
    expect(UgcNodeVersionPolicy.supports('not-node'), isFalse);
  });
}
