namespace Imperial2030.Shared.Constants;

public static class RondelData
{
    // The Rondel has 8 slots; Production and Maneuver each appear twice (once on either side of the wheel).
    public const int TaxationSlot = 0;
    public const int FactorySlot = 1;
    public const int ProductionSlot1 = 2;
    public const int ManeuverSlot1 = 3;
    public const int InvestorSlot = 4;
    public const int ImportSlot = 5;
    public const int ProductionSlot2 = 6;
    public const int ManeuverSlot2 = 7;
    public const int SlotCount = 8;

    // Moving up to 3 slots clockwise is free; each slot beyond that costs money (scaled by power).
    public const int FreeMoveDistance = 3;
    public const int MaxMoveDistance = 6;

    /// <summary>
    /// Power points per point of Power Factor on the scoring track. Source: Imperial-2030-Rules.pdf p.6 —
    /// "If for example a nation has reached 17 power points and the Power Factor therefore amounts to 3".
    /// </summary>
    public const int PowerPerFactorPoint = 5;

    /// <summary>A nation's Power Factor, as read off the scoring track.</summary>
    public static int GetPowerFactor(int power) => power / PowerPerFactorPoint;

    /// <summary>
    /// Spaces from <paramref name="fromSlot"/> to <paramref name="toSlot"/>, counted clockwise — the only
    /// direction the marker may move (p.6: "remaining in the same space is not allowed").
    /// </summary>
    public static int GetMoveDistance(int fromSlot, int toSlot) => (toSlot - fromSlot + SlotCount) % SlotCount;

    /// <summary>
    /// What the government pays the bank to move its marker to <paramref name="toSlot"/>.
    ///
    /// Source: Imperial-2030-Rules.pdf p.6 — "The nation marker may be moved to one of the three spaces
    /// ahead at no cost; for each additional space past the first three the player who leads the government
    /// of that nation has to pay to the bank: (1 + Power Factor on scoring track) in million."
    ///
    /// Only the *rule* lives here, as in <see cref="TaxationRules"/>: each caller supplies its own inputs,
    /// because they read them from different models (EF entities on the server, DTOs on the client). This
    /// arithmetic was previously written out by hand at eleven call sites.
    ///
    /// A null <paramref name="fromSlot"/> means the marker is not on the rondel yet, which is not a move
    /// and costs nothing. Distance is NOT validated against <see cref="MaxMoveDistance"/> here — that is a
    /// legality check the callers that need it make separately, and it has its own error message.
    /// </summary>
    public static int GetMoveCost(int? fromSlot, int toSlot, int power)
    {
        if (fromSlot == null) return 0;

        int distance = GetMoveDistance(fromSlot.Value, toSlot);
        if (distance <= FreeMoveDistance) return 0;

        return (distance - FreeMoveDistance) * (1 + GetPowerFactor(power));
    }

    public static bool IsProductionSlot(int index) => index == ProductionSlot1 || index == ProductionSlot2;
    public static bool IsManeuverSlot(int index) => index == ManeuverSlot1 || index == ManeuverSlot2;

    public static string GetSlotName(int index) => index switch
    {
        0 => "Taxation",
        1 => "Factory",
        2 => "Production",
        3 => "Maneuver",
        4 => "Investor",
        5 => "Import",
        6 => "Production",
        7 => "Maneuver",
        _ => $"Slot {index}"
    };

    public static string GetSlotColor(int index) => index switch
    {
        0 => "#f1c40f",
        1 => "#1f3a93",
        2 => "#7f8c8d",
        3 => "#2ecc71",
        4 => "#3498db",
        5 => "#e67e22",
        6 => "#7f8c8d",
        7 => "#2ecc71",
        _ => "#000000"
    };

    public static (string Name, string Color) GetSlotInfo(int index) => (GetSlotName(index), GetSlotColor(index));
}
