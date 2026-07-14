import 'package:launcher_domain/launcher_domain.dart';
import 'package:test/test.dart';

void main() {
  test('launcher compatibility versions match the bundled runtime', () {
    expect(RobotopiaRuntimeVersions.loaderVersion, '0.2.0');
    expect(RobotopiaRuntimeVersions.sdkVersion, '0.1.3');
  });
}
