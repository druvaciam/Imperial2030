using Imperial2030.Shared.Models;

namespace Imperial2030.Shared.Constants;

public static class NationData
{
    public static string GetColor(Nation nation) => nation switch
    {
        Nation.Russia => "#800080", // Purple
        Nation.China => "#FFD700", // Gold/Yellow
        Nation.India => "#000000", // Black
        Nation.Brazil => "#006400", // DarkGreen
        Nation.USA => "#FF4500", // OrangeRed
        Nation.Europe => "#0000FF", // Blue
        _ => "#808080" // Gray fallback
    };

    public static int GetMaxArmies(Nation nation) => nation switch
    {
        Nation.China => 10, // Yellow
        Nation.USA => 6,    // Red
        _ => 8
    };

    public static int GetMaxFleets(Nation nation) => nation switch
    {
        Nation.China => 6,  // Yellow
        Nation.USA => 10,   // Red
        _ => 8
    };
}
