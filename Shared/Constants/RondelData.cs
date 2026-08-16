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
