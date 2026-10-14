#include "single_instance.h"

#include <sddl.h>

#include <utility>
#include <vector>

namespace {

constexpr wchar_t kWindowClass[] = L"TopiaForgeLauncherInstance";
constexpr wchar_t kNamePrefix[] = L"TopiaForge.Launcher.";
constexpr wchar_t kScheme[] = L"topiaforge:";
constexpr size_t kSchemeLength = 11;

// The two kinds of WM_COPYDATA a later start sends.
constexpr ULONG_PTR kLinkMessage = 0x54464C4B;      // 'TFLK': a session link
constexpr ULONG_PTR kActivateMessage = 0x54464143;  // 'TFAC': come forward

constexpr UINT kHandOffTimeoutMs = 5000;
constexpr int kFindAttempts = 20;
constexpr DWORD kFindIntervalMs = 100;

bool IsScope(const std::wstring& scope) {
  if (scope.size() != 32) {
    return false;
  }
  for (const wchar_t unit : scope) {
    if (!((unit >= L'0' && unit <= L'9') || (unit >= L'a' && unit <= L'f'))) {
      return false;
    }
  }
  return true;
}

// The user SID in |process|'s token, or nothing.
std::vector<BYTE> ProcessUserSid(HANDLE process) {
  std::vector<BYTE> sid;
  HANDLE token = nullptr;
  if (!::OpenProcessToken(process, TOKEN_QUERY, &token)) {
    return sid;
  }
  DWORD size = 0;
  ::GetTokenInformation(token, TokenUser, nullptr, 0, &size);
  std::vector<BYTE> buffer(size);
  if (size > 0 &&
      ::GetTokenInformation(token, TokenUser, buffer.data(), size, &size)) {
    const auto* user = reinterpret_cast<const TOKEN_USER*>(buffer.data());
    const DWORD length = ::GetLengthSid(user->User.Sid);
    const auto* bytes = static_cast<const BYTE*>(user->User.Sid);
    sid.assign(bytes, bytes + length);
  }
  ::CloseHandle(token);
  return sid;
}

std::wstring CurrentUserSidString() {
  std::vector<BYTE> sid = ProcessUserSid(::GetCurrentProcess());
  if (sid.empty()) {
    return std::wstring();
  }
  LPWSTR text = nullptr;
  if (!::ConvertSidToStringSidW(reinterpret_cast<PSID>(sid.data()), &text)) {
    return std::wstring();
  }
  std::wstring result(text);
  ::LocalFree(text);
  return result;
}

// Whether |window| belongs to a process of the current user. Another user's
// process could name a window the same way to catch links meant for this one.
bool OwnedByCurrentUser(HWND window) {
  DWORD process_id = 0;
  ::GetWindowThreadProcessId(window, &process_id);
  if (process_id == 0) {
    return false;
  }
  HANDLE process =
      ::OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, FALSE, process_id);
  if (process == nullptr) {
    return false;
  }
  std::vector<BYTE> owner = ProcessUserSid(process);
  ::CloseHandle(process);
  std::vector<BYTE> current = ProcessUserSid(::GetCurrentProcess());
  return !owner.empty() && !current.empty() &&
         ::EqualSid(reinterpret_cast<PSID>(owner.data()),
                    reinterpret_cast<PSID>(current.data()));
}

// Exactly |length| UTF-16 code units as UTF-8, or nothing when they are not
// valid UTF-16. An embedded NUL is kept, so Dart sees what was sent.
std::optional<std::string> ExactUtf8(const wchar_t* text, size_t length) {
  if (length == 0) {
    return std::string();
  }
  const int units = static_cast<int>(length);
  const int bytes = ::WideCharToMultiByte(CP_UTF8, WC_ERR_INVALID_CHARS, text,
                                          units, nullptr, 0, nullptr, nullptr);
  if (bytes <= 0) {
    return std::nullopt;
  }
  std::string result(static_cast<size_t>(bytes), '\0');
  if (::WideCharToMultiByte(CP_UTF8, WC_ERR_INVALID_CHARS, text, units,
                            result.data(), bytes, nullptr,
                            nullptr) != bytes) {
    return std::nullopt;
  }
  return result;
}

}  // namespace

SingleInstance::~SingleInstance() {
  if (window_ != nullptr) {
    ::DestroyWindow(window_);
  }
  if (owns_mutex_) {
    ::ReleaseMutex(mutex_);
  }
  if (mutex_ != nullptr) {
    ::CloseHandle(mutex_);
  }
}

bool SingleInstance::HasSessionLinkScheme(const wchar_t* text, size_t length) {
  if (text == nullptr || length < kSchemeLength) {
    return false;
  }
  for (size_t index = 0; index < kSchemeLength; ++index) {
    wchar_t unit = text[index];
    if (unit >= L'A' && unit <= L'Z') {
      unit = static_cast<wchar_t>(unit - L'A' + L'a');
    }
    if (unit != kScheme[index]) {
      return false;
    }
  }
  return true;
}

SingleInstance::Claim SingleInstance::TryClaim(const std::wstring& scope) {
  if (window_ != nullptr) {
    return Claim::kPrimary;
  }
  if (!IsScope(scope)) {
    return Claim::kUnavailable;
  }
  if (mutex_ == nullptr) {
    const std::wstring user = CurrentUserSidString();
    if (user.empty()) {
      return Claim::kUnavailable;
    }
    name_ = std::wstring(kNamePrefix) + user + L"." + scope;
    // Local\ is this logon session; the SID keeps users apart within it.
    mutex_ = ::CreateMutexW(nullptr, FALSE, (L"Local\\" + name_).c_str());
    if (mutex_ == nullptr) {
      name_.clear();
      return Claim::kUnavailable;
    }
  } else if (name_.size() < scope.size() ||
             name_.compare(name_.size() - scope.size(), scope.size(),
                           scope) != 0) {
    // One process claims one scope.
    return Claim::kUnavailable;
  }
  const DWORD wait = ::WaitForSingleObject(mutex_, 0);
  if (wait == WAIT_TIMEOUT) {
    return Claim::kSecondary;
  }
  // WAIT_ABANDONED: the last owner exited without releasing it.
  if (wait != WAIT_OBJECT_0 && wait != WAIT_ABANDONED) {
    return Claim::kUnavailable;
  }
  owns_mutex_ = true;
  if (!CreateMessageWindow()) {
    ::ReleaseMutex(mutex_);
    owns_mutex_ = false;
    return Claim::kUnavailable;
  }
  return Claim::kPrimary;
}

bool SingleInstance::HandOff(const std::optional<std::wstring>& link) {
  if (name_.empty() || window_ != nullptr) {
    return false;
  }
  const HWND owner = FindOwner();
  if (owner == nullptr) {
    return false;
  }
  // Windows lets a background process come forward only when the foreground
  // one allows it, and the start the user just made is in the foreground.
  DWORD owner_process = 0;
  ::GetWindowThreadProcessId(owner, &owner_process);
  ::AllowSetForegroundWindow(owner_process);

  COPYDATASTRUCT data{};
  std::wstring bounded;
  if (link.has_value() &&
      HasSessionLinkScheme(link->c_str(), link->size())) {
    // One unit past the limit is enough for the owner to call it too long.
    bounded = link->substr(0, kMaximumLinkLength + 1);
    data.dwData = kLinkMessage;
    data.cbData = static_cast<DWORD>(bounded.size() * sizeof(wchar_t));
    data.lpData = bounded.data();
  } else {
    data.dwData = kActivateMessage;
  }
  DWORD_PTR accepted = FALSE;
  const LRESULT sent = ::SendMessageTimeoutW(
      owner, WM_COPYDATA, 0, reinterpret_cast<LPARAM>(&data),
      SMTO_ABORTIFHUNG | SMTO_ERRORONEXIT, kHandOffTimeoutMs, &accepted);
  return sent != 0 && accepted == TRUE;
}

void SingleInstance::SetHandlers(Handler on_link, Handler on_refusal) {
  on_link_ = std::move(on_link);
  on_refusal_ = std::move(on_refusal);
}

void SingleInstance::SetReady() {
  ready_ = true;
  if (held_.has_value()) {
    Delivery held = std::move(*held_);
    held_.reset();
    Deliver(std::move(held));
  }
}

bool SingleInstance::CreateMessageWindow() {
  WNDCLASSEXW window_class{};
  window_class.cbSize = sizeof(window_class);
  window_class.lpfnWndProc = SingleInstance::WindowProc;
  window_class.hInstance = ::GetModuleHandleW(nullptr);
  window_class.lpszClassName = kWindowClass;
  if (::RegisterClassExW(&window_class) == 0 &&
      ::GetLastError() != ERROR_CLASS_ALREADY_EXISTS) {
    return false;
  }
  window_ = ::CreateWindowExW(0, kWindowClass, name_.c_str(), 0, 0, 0, 0, 0,
                              HWND_MESSAGE, nullptr, window_class.hInstance,
                              this);
  return window_ != nullptr;
}

// static
LRESULT CALLBACK SingleInstance::WindowProc(HWND window,
                                            UINT message,
                                            WPARAM wparam,
                                            LPARAM lparam) {
  if (message == WM_NCCREATE) {
    const auto* create = reinterpret_cast<const CREATESTRUCTW*>(lparam);
    ::SetWindowLongPtrW(window, GWLP_USERDATA,
                        reinterpret_cast<LONG_PTR>(create->lpCreateParams));
  } else if (message == WM_COPYDATA) {
    auto* self = reinterpret_cast<SingleInstance*>(
        ::GetWindowLongPtrW(window, GWLP_USERDATA));
    const auto* data = reinterpret_cast<const COPYDATASTRUCT*>(lparam);
    if (self == nullptr || data == nullptr) {
      return FALSE;
    }
    return self->Receive(*data) ? TRUE : FALSE;
  }
  return ::DefWindowProcW(window, message, wparam, lparam);
}

bool SingleInstance::Receive(const COPYDATASTRUCT& data) {
  if (data.dwData == kActivateMessage) {
    if (data.cbData != 0) {
      return false;
    }
    BringForward();
    return true;
  }
  if (data.dwData != kLinkMessage || data.lpData == nullptr ||
      data.cbData == 0 || data.cbData % sizeof(wchar_t) != 0) {
    return false;
  }
  // Another process wrote this buffer. Only the scheme is read in place, and
  // the length is checked before any of it is copied.
  const auto* text = static_cast<const wchar_t*>(data.lpData);
  const size_t length = data.cbData / sizeof(wchar_t);
  if (!HasSessionLinkScheme(text, length)) {
    return false;
  }
  if (length > kMaximumLinkLength) {
    Deliver(Delivery{true, "tooLong"});
  } else if (std::optional<std::string> link = ExactUtf8(text, length)) {
    Deliver(Delivery{false, std::move(*link)});
  } else {
    Deliver(Delivery{true, "invalid"});
  }
  BringForward();
  return true;
}

void SingleInstance::Deliver(Delivery delivery) {
  if (!ready_) {
    // Until Dart listens, the newest link replaces the one held.
    held_ = std::move(delivery);
    return;
  }
  const Handler& handler = delivery.refused ? on_refusal_ : on_link_;
  if (handler) {
    handler(delivery.value);
  }
}

void SingleInstance::BringForward() const {
  if (main_window_ == nullptr || !::IsWindowVisible(main_window_)) {
    return;
  }
  if (::IsIconic(main_window_)) {
    ::ShowWindow(main_window_, SW_RESTORE);
  }
  // Allowed because the start that handed off called AllowSetForegroundWindow.
  ::SetForegroundWindow(main_window_);
}

HWND SingleInstance::FindOwner() const {
  for (int attempt = 0; attempt < kFindAttempts; ++attempt) {
    HWND candidate = nullptr;
    while ((candidate = ::FindWindowExW(HWND_MESSAGE, candidate, kWindowClass,
                                        name_.c_str())) != nullptr) {
      if (OwnedByCurrentUser(candidate)) {
        return candidate;
      }
    }
    // The owner creates its window right after its mutex; give it a moment.
    ::Sleep(kFindIntervalMs);
  }
  return nullptr;
}
