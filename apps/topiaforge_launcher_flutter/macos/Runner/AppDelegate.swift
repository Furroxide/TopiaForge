import Cocoa
import FlutterMacOS

@main
class AppDelegate: FlutterAppDelegate {
  override func applicationShouldTerminateAfterLastWindowClosed(_ sender: NSApplication) -> Bool {
    return true
  }

  override func applicationSupportsSecureRestorableState(_ app: NSApplication) -> Bool {
    return true
  }

  // LaunchServices keeps one launcher running and hands it every
  // topiaforge:// link the system opens. Other URLs go on to the plugins.
  override func application(_ application: NSApplication, open urls: [URL]) {
    var others: [URL] = []
    for url in urls {
      if !SessionLinkChannel.shared.receive(url) {
        others.append(url)
      }
    }
    if !others.isEmpty {
      super.application(application, open: others)
    }
  }
}

/// Carries topiaforge:// links from macOS to Dart on the launcher's instance
/// channel (lib/src/launcher_instance_channel.dart). A link is untrusted text
/// from another program: it must use the topiaforge scheme, it is bounded, and
/// it only ever reaches Dart, which parses it.
final class SessionLinkChannel {
  static let shared = SessionLinkChannel()

  /// A session link is at most 512 characters. The margin lets a slightly
  /// longer one through, so that Dart reports it as too long itself.
  static let maximumLinkLength = 512 + 64

  private var channel: FlutterMethodChannel?
  private var ready = false
  private var held: (method: String, value: String)?

  func attach(binaryMessenger: FlutterBinaryMessenger) {
    let channel = FlutterMethodChannel(
      name: "io.github.furroxide.topiaforge.launcher/instance",
      binaryMessenger: binaryMessenger)
    channel.setMethodCallHandler { [weak self] call, result in
      // LaunchServices keeps the launcher single, so Dart has nothing to
      // claim or hand off here; it only says when it listens.
      guard call.method == "ready" else {
        result(FlutterMethodNotImplemented)
        return
      }
      self?.markReady()
      result(nil)
    }
    self.channel = channel
  }

  /// Takes |url| when it is a topiaforge link, and says whether it did.
  func receive(_ url: URL) -> Bool {
    guard url.scheme?.lowercased() == "topiaforge" else {
      return false
    }
    let text = url.absoluteString
    if text.utf16.count > SessionLinkChannel.maximumLinkLength {
      send(method: "linkRefused", value: "tooLong")
    } else {
      send(method: "link", value: text)
    }
    return true
  }

  private func markReady() {
    ready = true
    if let held = held {
      self.held = nil
      send(method: held.method, value: held.value)
    }
  }

  private func send(method: String, value: String) {
    guard ready, let channel = channel else {
      // Until Dart listens, the newest link replaces the one held.
      held = (method: method, value: value)
      return
    }
    channel.invokeMethod(method, arguments: value)
  }
}
