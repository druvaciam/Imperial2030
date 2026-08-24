namespace Imperial2030.Shared.Constants;

public static class GameConstants
{
    // Player-name recorded on a logged GameAction when it has no single human/bot actor to attribute to
    // (a purely system-computed consequence) or when the real identity couldn't be resolved. Centralized
    // here instead of scattered "System" string literals so every read site (GameReplayService's actor
    // resolution, diagnostics, UI) agrees on the exact value.
    public const string SystemPlayerName = "System";

    // Maximum persisted length of Game.Name, mirrored by its [MaxLength] attribute. Referenced rather than
    // repeated as a literal so the import-time name builder (GamesController.BuildImportedGameName, which
    // has to truncate to fit) can't drift out of sync with the column and start failing inserts.
    public const int MaxGameNameLength = 50;

    // A nation reaching this many power points ends the game immediately (Imperial 2030 rules, "Object of
    // the Game / End of the Game"), and it is also the top of the scoring track, so power is clamped here.
    public const int MaxPowerPoints = 25;

    // Role claim carried by tokens from AuthController.GuestLogin. A guest is a throwaway identity with no
    // backing ApplicationUser row, so it may browse and spectate but not create or join games.
    //
    // Centralized because this one string is load-bearing in two places that MUST agree: Program.cs's
    // OnTokenValidated skips its user-store existence check for this role (a guest has no row to find),
    // and every `User.IsInRole(...)` gate in GamesController refuses guest writes. When these were
    // separate "Guest" literals and only one side knew about the role, guest tokens authenticated
    // nowhere and the authorization gates became unreachable.
    public const string GuestRole = "Guest";
}
