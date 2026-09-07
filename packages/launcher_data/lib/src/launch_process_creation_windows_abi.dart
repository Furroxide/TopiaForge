part of 'launch_process_control.dart';

// Layout follows the native pointer width/alignment on Windows x86 and x64.
final class _WindowsStartupInfo extends Struct {
  @Uint32()
  external int cb;
  external Pointer<Uint16> reserved;
  external Pointer<Uint16> desktop;
  external Pointer<Uint16> title;
  @Uint32()
  external int x;
  @Uint32()
  external int y;
  @Uint32()
  external int xSize;
  @Uint32()
  external int ySize;
  @Uint32()
  external int xCountChars;
  @Uint32()
  external int yCountChars;
  @Uint32()
  external int fillAttribute;
  @Uint32()
  external int flags;
  @Uint16()
  external int showWindow;
  @Uint16()
  external int reservedBytes;
  external Pointer<Uint8> reservedData;
  external Pointer<Void> standardInput;
  external Pointer<Void> standardOutput;
  external Pointer<Void> standardError;
}

final class _WindowsProcessInformation extends Struct {
  external Pointer<Void> process;
  external Pointer<Void> thread;
  @Uint32()
  external int processId;
  @Uint32()
  external int threadId;
}

final class _WindowsCreationApi {
  static final library = _WindowsProcessApi.library;
  final create = library
      .lookupFunction<
        Int32 Function(
          Pointer<Uint16>,
          Pointer<Uint16>,
          Pointer<Void>,
          Pointer<Void>,
          Int32,
          Uint32,
          Pointer<Void>,
          Pointer<Uint16>,
          Pointer<_WindowsStartupInfo>,
          Pointer<_WindowsProcessInformation>,
        ),
        int Function(
          Pointer<Uint16>,
          Pointer<Uint16>,
          Pointer<Void>,
          Pointer<Void>,
          int,
          int,
          Pointer<Void>,
          Pointer<Uint16>,
          Pointer<_WindowsStartupInfo>,
          Pointer<_WindowsProcessInformation>,
        )
      >('CreateProcessW');
  final resume = library
      .lookupFunction<
        Uint32 Function(Pointer<Void>),
        int Function(Pointer<Void>)
      >('ResumeThread');
  final compare = library
      .lookupFunction<
        Int32 Function(Pointer<Uint16>, Int32, Pointer<Uint16>, Int32, Int32),
        int Function(Pointer<Uint16>, int, Pointer<Uint16>, int, int)
      >('CompareStringOrdinal');
}
