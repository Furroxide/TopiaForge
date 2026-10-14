#ifndef RUNNER_SINGLE_INSTANCE_H_
#define RUNNER_SINGLE_INSTANCE_H_

#include <windows.h>

#include <functional>
#include <optional>
#include <string>

// Keeps one launcher per user and data root.
//
// The first process to claim a scope owns it: it holds a named mutex and a
// message-only window. A later start with the same scope hands its session
// link to that window through WM_COPYDATA, and exits. The Dart side decides
// when to claim and when to hand off (lib/src/launcher_instance.dart); this
// class only does what Windows needs for that.
//
// A link from another process is untrusted. Only its scheme is read in
// place, its size is checked before any of it is copied, and it reaches Dart
// as text and nothing else. Nothing here passes it to a shell.
class SingleInstance {
 public:
  enum class Claim { kPrimary, kSecondary, kUnavailable };

  // A session link is at most 512 characters. The margin lets a slightly
  // longer one through, so that Dart reports it as too long itself.
  static constexpr size_t kMaximumLinkLength = 512 + 64;

  using Handler = std::function<void(const std::string& value)>;

  SingleInstance() = default;
  ~SingleInstance();

  SingleInstance(const SingleInstance&) = delete;
  SingleInstance& operator=(const SingleInstance&) = delete;

  // Claims |scope|, 32 lowercase hexadecimal characters that name the data
  // root. The current user's SID goes into the name, so users never meet.
  Claim TryClaim(const std::wstring& scope);

  // Hands |link| to the owner of the claimed scope, or without a link only
  // asks it to come forward. Returns true when the owner took it.
  bool HandOff(const std::optional<std::wstring>& link);

  // The window that comes forward when a link arrives.
  void SetMainWindow(HWND window) { main_window_ = window; }

  // Where links and refusals go once Dart listens.
  void SetHandlers(Handler on_link, Handler on_refusal);

  // Dart listens: the newest link held until now goes to it.
  void SetReady();

  // Whether |text| starts with the topiaforge: scheme, in ASCII case only.
  // It reads at most the scheme's eleven code units.
  static bool HasSessionLinkScheme(const wchar_t* text, size_t length);

 private:
  struct Delivery {
    bool refused = false;
    // The link, or the reason it was refused.
    std::string value;
  };

  static LRESULT CALLBACK WindowProc(HWND window,
                                     UINT message,
                                     WPARAM wparam,
                                     LPARAM lparam);

  bool CreateMessageWindow();
  bool Receive(const COPYDATASTRUCT& data);
  void Deliver(Delivery delivery);
  void BringForward() const;
  HWND FindOwner() const;

  std::wstring name_;
  HANDLE mutex_ = nullptr;
  bool owns_mutex_ = false;
  HWND window_ = nullptr;
  HWND main_window_ = nullptr;
  bool ready_ = false;
  std::optional<Delivery> held_;
  Handler on_link_;
  Handler on_refusal_;
};

#endif  // RUNNER_SINGLE_INSTANCE_H_
