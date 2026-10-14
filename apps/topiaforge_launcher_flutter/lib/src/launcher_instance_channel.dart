import 'package:flutter/services.dart';
import 'package:launcher_domain/launcher_domain.dart';

import 'launcher_instance.dart';
import 'session_link_inbox.dart';

/// The runner's side of single instance and session links, on one channel.
///
/// Dart calls `claim` with the scope, `handOff` with the link, and `ready`
/// once it listens, which releases the newest link the runner held. The
/// runner calls `link` with the text of a link that another start handed
/// over or the system opened, and `linkRefused` with a reason when it
/// refused one before reading it. Every link goes to the [SessionLinkInbox],
/// and the domain reads it there; nothing here interprets it.
final class LauncherInstanceChannel implements LauncherInstancePort {
  LauncherInstanceChannel({
    required SessionLinkInbox inbox,
    MethodChannel channel = const MethodChannel(name),
  }) : _inbox = inbox,
       _channel = channel {
    _channel.setMethodCallHandler(_receive);
  }

  static const name = 'io.github.furroxide.topiaforge.launcher/instance';

  final SessionLinkInbox _inbox;
  final MethodChannel _channel;

  @override
  Future<LauncherInstanceClaim> claim(String scope) async {
    try {
      final role = await _channel.invokeMethod<String>('claim', {
        'scope': scope,
      });
      return switch (role) {
        'primary' => LauncherInstanceClaim.primary,
        'secondary' => LauncherInstanceClaim.secondary,
        _ => LauncherInstanceClaim.notSupported,
      };
    } on MissingPluginException {
      return LauncherInstanceClaim.notSupported;
    } on PlatformException {
      return LauncherInstanceClaim.notSupported;
    }
  }

  @override
  Future<bool> handOff(String? link) async {
    try {
      return await _channel.invokeMethod<bool>('handOff', {'link': link}) ??
          false;
    } on MissingPluginException {
      return false;
    } on PlatformException {
      return false;
    }
  }

  /// Tells the runner that Dart listens, so it passes on the link it held.
  Future<void> ready() async {
    try {
      await _channel.invokeMethod<void>('ready');
    } on MissingPluginException {
      // No runner side on this platform: nothing is held.
    } on PlatformException {
      // Nothing more to do; links still arrive as they come.
    }
  }

  Future<Object?> _receive(MethodCall call) async {
    final argument = call.arguments;
    switch (call.method) {
      case 'link' when argument is String:
        _inbox.deliver(
          SessionLinkArrival(argument, source: SessionLinkSource.forwarded),
        );
        return null;
      case 'linkRefused':
        _inbox.deliver(
          SessionLinkArrival.refused(
            argument == 'tooLong'
                ? SessionLinkRefusal.tooLong
                : SessionLinkRefusal.invalid,
            source: SessionLinkSource.forwarded,
          ),
        );
        return null;
    }
    throw MissingPluginException('${call.method} is not a launcher call.');
  }
}
