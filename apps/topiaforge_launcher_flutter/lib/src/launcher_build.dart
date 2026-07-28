abstract final class TopiaForgeLauncherBuild {
  static const version = String.fromEnvironment(
    'TOPIAFORGE_PRODUCT_VERSION',
    defaultValue: '1.0.0-rc.1',
  );

  static const updaterVersion = '1.0.0-rc.1';
}
