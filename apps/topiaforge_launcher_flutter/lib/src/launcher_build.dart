abstract final class TopiaForgeLauncherBuild {
  static const version = String.fromEnvironment(
    'TOPIAFORGE_PRODUCT_VERSION',
    defaultValue: '0.1.0-rc.1',
  );

  static const updaterVersion = '0.1.0-rc.1';
}
