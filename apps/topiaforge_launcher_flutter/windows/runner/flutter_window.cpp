#include "flutter_window.h"

#include <flutter/standard_method_codec.h>

#include <optional>
#include <utility>
#include <variant>

#include "flutter/generated_plugin_registrant.h"

namespace {

// The channel Dart uses for single instance and session links
// (lib/src/launcher_instance_channel.dart).
constexpr char kInstanceChannel[] =
    "io.github.furroxide.topiaforge.launcher/instance";

// Nothing Dart sends here is anywhere near this long.
constexpr size_t kMaximumArgumentBytes = 256 * 1024;

// The string argument |key| of a call, or null.
const std::string* StringArgument(const flutter::EncodableValue* arguments,
                                  const char* key) {
  const auto* map = std::get_if<flutter::EncodableMap>(arguments);
  if (map == nullptr) {
    return nullptr;
  }
  const auto found = map->find(flutter::EncodableValue(key));
  if (found == map->end()) {
    return nullptr;
  }
  return std::get_if<std::string>(&found->second);
}

std::optional<std::wstring> Utf16FromUtf8(const std::string& text) {
  if (text.empty()) {
    return std::wstring();
  }
  if (text.size() > kMaximumArgumentBytes) {
    return std::nullopt;
  }
  const int length = static_cast<int>(text.size());
  const int units = ::MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS,
                                          text.data(), length, nullptr, 0);
  if (units <= 0) {
    return std::nullopt;
  }
  std::wstring result(static_cast<size_t>(units), L'\0');
  if (::MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, text.data(), length,
                            result.data(), units) != units) {
    return std::nullopt;
  }
  return result;
}

}  // namespace

FlutterWindow::FlutterWindow(const flutter::DartProject& project)
    : project_(project) {}

FlutterWindow::~FlutterWindow() {
  // System.exitApplication ends the message loop while this window still
  // exists, as when a later start hands off and quits. Tear down here, while
  // this is still a FlutterWindow, so OnDestroy releases the controller
  // before the window goes. Left to member destruction, the controller's own
  // teardown would send messages back through a half-destroyed controller.
  Destroy();
}

bool FlutterWindow::OnCreate() {
  if (!Win32Window::OnCreate()) {
    return false;
  }

  RECT frame = GetClientArea();

  // The size here must match the window dimensions to avoid unnecessary surface
  // creation / destruction in the startup path.
  flutter_controller_ = std::make_unique<flutter::FlutterViewController>(
      frame.right - frame.left, frame.bottom - frame.top, project_);
  // Ensure that basic setup of the controller was successful.
  if (!flutter_controller_->engine() || !flutter_controller_->view()) {
    return false;
  }
  RegisterPlugins(flutter_controller_->engine());
  SetChildContent(flutter_controller_->view()->GetNativeWindow());

  // Dart claims the instance before it shows anything, and a later start
  // hands its link over and exits without ever showing this window.
  instance_channel_ =
      std::make_unique<flutter::MethodChannel<flutter::EncodableValue>>(
          flutter_controller_->engine()->messenger(), kInstanceChannel,
          &flutter::StandardMethodCodec::GetInstance());
  instance_channel_->SetMethodCallHandler(
      [this](const flutter::MethodCall<flutter::EncodableValue>& call,
             std::unique_ptr<flutter::MethodResult<flutter::EncodableValue>>
                 result) { HandleInstanceCall(call, std::move(result)); });
  single_instance_.SetMainWindow(GetHandle());
  single_instance_.SetHandlers(
      [this](const std::string& link) { SendToDart("link", link); },
      [this](const std::string& reason) { SendToDart("linkRefused", reason); });

  flutter_controller_->engine()->SetNextFrameCallback([&]() {
    this->Show();
  });

  // Flutter can complete the first frame before the "show window" callback is
  // registered. The following call ensures a frame is pending to ensure the
  // window is shown. It is a no-op if the first frame hasn't completed yet.
  flutter_controller_->ForceRedraw();

  return true;
}

void FlutterWindow::OnDestroy() {
  single_instance_.SetHandlers(nullptr, nullptr);
  instance_channel_ = nullptr;
  if (flutter_controller_) {
    flutter_controller_ = nullptr;
  }

  Win32Window::OnDestroy();
}

LRESULT
FlutterWindow::MessageHandler(HWND hwnd, UINT const message,
                              WPARAM const wparam,
                              LPARAM const lparam) noexcept {
  // Give Flutter, including plugins, an opportunity to handle window messages.
  if (flutter_controller_) {
    std::optional<LRESULT> result =
        flutter_controller_->HandleTopLevelWindowProc(hwnd, message, wparam,
                                                      lparam);
    if (result) {
      return *result;
    }
  }

  switch (message) {
    case WM_FONTCHANGE:
      flutter_controller_->engine()->ReloadSystemFonts();
      break;
  }

  return Win32Window::MessageHandler(hwnd, message, wparam, lparam);
}

void FlutterWindow::HandleInstanceCall(
    const flutter::MethodCall<flutter::EncodableValue>& call,
    std::unique_ptr<flutter::MethodResult<flutter::EncodableValue>> result) {
  const std::string& method = call.method_name();
  if (method == "claim") {
    const std::string* scope = StringArgument(call.arguments(), "scope");
    const std::optional<std::wstring> wide =
        scope == nullptr ? std::nullopt : Utf16FromUtf8(*scope);
    if (!wide.has_value()) {
      result->Error("invalid-scope", "A claim needs its scope.");
      return;
    }
    const char* role = "unavailable";
    switch (single_instance_.TryClaim(*wide)) {
      case SingleInstance::Claim::kPrimary:
        role = "primary";
        break;
      case SingleInstance::Claim::kSecondary:
        role = "secondary";
        break;
      case SingleInstance::Claim::kUnavailable:
        break;
    }
    result->Success(flutter::EncodableValue(role));
  } else if (method == "handOff") {
    const std::string* link = StringArgument(call.arguments(), "link");
    std::optional<std::wstring> wide;
    if (link != nullptr) {
      wide = Utf16FromUtf8(*link);
    }
    result->Success(flutter::EncodableValue(single_instance_.HandOff(wide)));
  } else if (method == "ready") {
    single_instance_.SetReady();
    result->Success();
  } else {
    result->NotImplemented();
  }
}

void FlutterWindow::SendToDart(const char* method, const std::string& value) {
  if (instance_channel_) {
    instance_channel_->InvokeMethod(
        method, std::make_unique<flutter::EncodableValue>(value));
  }
}
