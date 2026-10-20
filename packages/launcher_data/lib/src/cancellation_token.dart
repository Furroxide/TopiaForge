/// A one-shot request to stop long network work, shared by the HTTPS helpers
/// and the multiplayer clients.
///
/// The token is the domain's, so domain interfaces such as
/// `MultiplayerContentHost` take the same one; this library keeps the data
/// layer's imports and its barrel export as they were.
library;

export 'package:launcher_domain/launcher_domain.dart'
    show
        CancellationRegistration,
        CancellationToken,
        OperationCancelledException;
