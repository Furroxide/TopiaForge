#include <flutter/dart_project.h>
#include <flutter/flutter_view_controller.h>
#include <windows.h>

#include <optional>

#include "flutter_window.h"
#include "single_instance.h"
#include "utils.h"

int APIENTRY wWinMain(_In_ HINSTANCE instance, _In_opt_ HINSTANCE prev,
                      _In_ wchar_t *command_line, _In_ int show_command) {
  // Attach to console when present (e.g., 'flutter run') or create a
  // new console when running with a debugger.
  if (!::AttachConsole(ATTACH_PARENT_PROCESS) && ::IsDebuggerPresent()) {
    CreateAndAttachConsole();
  }

  // Initialize COM, so that it is available for use in the library and/or
  // plugins.
  ::CoInitializeEx(nullptr, COINIT_APARTMENTTHREADED);

  flutter::DartProject project(L"data");

  // A link start passes its link as the only argument, read whole from the
  // raw command line, so a quote inside the link cannot add arguments. A
  // link that is not valid UTF-16 converts to nothing; it goes on as the
  // bare scheme, which Dart refuses as damaged, so it never becomes a start
  // without a link.
  std::vector<std::string> command_line_arguments;
  if (const std::optional<std::wstring> link =
          SingleInstance::LinkFromCommandLine(::GetCommandLineW())) {
    std::string text = Utf8FromUtf16(link->c_str());
    command_line_arguments.push_back(text.empty() ? "topiaforge:"
                                                  : std::move(text));
  } else {
    command_line_arguments = GetCommandLineArguments();
  }

  project.set_dart_entrypoint_arguments(std::move(command_line_arguments));

  FlutterWindow window(project);
  Win32Window::Point origin(10, 10);
  Win32Window::Size size(1280, 720);
  if (!window.Create(L"TopiaForge", origin, size)) {
    return EXIT_FAILURE;
  }
  window.SetQuitOnClose(true);

  ::MSG msg;
  while (::GetMessage(&msg, nullptr, 0, 0)) {
    ::TranslateMessage(&msg);
    ::DispatchMessage(&msg);
  }

  ::CoUninitialize();
  // WM_QUIT carries the code System.exitApplication asked for.
  return static_cast<int>(msg.wParam);
}
