/// Private acceptance identity measured from a Windows primary process token.
/// Paths are OS known folders, never values inferred from environment variables.
final class WindowsAcceptanceIdentity {
  const WindowsAcceptanceIdentity({
    required this.userSid,
    required this.logonId,
    required this.sessionId,
    required this.userProfile,
    required this.localAppDataLow,
  });
  final String userSid;
  final String logonId;
  final int sessionId;
  final String userProfile;
  final String localAppDataLow;

  Map<String, Object?> toJson() => {
    'userSid': userSid,
    'logonId': logonId,
    'sessionId': sessionId,
    'userProfile': userProfile,
    'localAppDataLow': localAppDataLow,
  };

  bool matches(WindowsAcceptanceIdentity other) =>
      userSid == other.userSid &&
      logonId == other.logonId &&
      sessionId == other.sessionId &&
      userProfile.toLowerCase() == other.userProfile.toLowerCase() &&
      localAppDataLow.toLowerCase() == other.localAppDataLow.toLowerCase();
}
