namespace Imperial2030.Shared.Constants;

public static class RondelData
{
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
