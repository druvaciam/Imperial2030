namespace Imperial2030.Server.Engine;

/// <summary>
/// Outcome of one game operation in <c>Server/Engine</c>.
///
/// An engine method validates, mutates the in-memory <see cref="Models.Game"/> and logs through
/// <see cref="Helpers.GameLogger"/>; it does not save, broadcast or decide what HTTP status to return.
/// A failed result carries the reason as text so the HTTP layer can hand it back as a 400 body exactly
/// as the inline <c>BadRequest("...")</c> strings did, and so the bot and training callers - which
/// used to skip validation entirely and apply their own copy of the rule - get the same answer the
/// endpoint would have given.
/// </summary>
public record EngineResult(bool Ok, string? Error = null)
{
    public static readonly EngineResult Success = new(true);

    public static EngineResult Fail(string error) => new(false, error);
}

/// <summary>
/// Outcome of a rondel move. Beyond pass/fail the caller needs to know whether the move was
/// intercepted by a Swiss Bank force-stop (in which case nothing moved and the turn is paused until the
/// responders answer), and what the move cost, for the reward shaping in training.
/// </summary>
public sealed record RondelMoveResult(
    bool Ok,
    string? Error = null,
    bool SwissBankIntercepted = false,
    int? PreviousSlot = null,
    int Cost = 0) : EngineResult(Ok, Error)
{
    public static RondelMoveResult Intercepted() => new(true, SwissBankIntercepted: true);

    public static RondelMoveResult Moved(int? previousSlot, int cost) => new(true, PreviousSlot: previousSlot, Cost: cost);

    public static new RondelMoveResult Fail(string error) => new(false, error);
}
