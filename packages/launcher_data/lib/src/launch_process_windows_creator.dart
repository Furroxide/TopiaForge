part of 'launch_process_control.dart';

/// Source-internal creator with immutable, per-operation native dependencies.
/// Production always constructs the real adapters. Tests may decorate calls on
/// their own creator without changing other launches, an environment, or globals.
final class WindowsLaunchProcessCreator {
  WindowsLaunchProcessCreator({
    WindowsProcessApi? processApi,
    WindowsCreationApi? creationApi,
  }) : _processApi = processApi ?? WindowsProcessApi(),
       _creationApi = creationApi ?? WindowsCreationApi();

  final WindowsProcessApi _processApi;
  final WindowsCreationApi _creationApi;

  Future<LaunchProcessReceipt> start({
    required String executable,
    required List<String> arguments,
    required String workingDirectory,
    required Map<String, String> environment,
    bool inheritParentEnvironment = true,
    WindowsAcceptanceIdentity? requiredWindowsIdentity,
  }) async {
    if (!Platform.isWindows) {
      throw UnsupportedError('Native Windows process creation is unavailable.');
    }
    final args = List<String>.of(arguments);
    final overrides = Map<String, String>.of(environment);
    _validateCreationInputs(executable, args, workingDirectory, overrides);
    return _createWindowsProcess(
      executable,
      args,
      workingDirectory,
      overrides,
      inheritParentEnvironment,
      requiredWindowsIdentity,
      _processApi,
      _creationApi,
    );
  }
}
