using System.Collections.Generic;

namespace Imperial2030.Shared.Constants;

public static class BondData
{
    // Cost -> Interest
    public static readonly Dictionary<int, int> BondValues = new Dictionary<int, int>
    {
        { 2, 1 },
        { 4, 2 },
        { 6, 3 },
        { 9, 4 },
        { 12, 5 },
        { 16, 6 },
        { 20, 7 },
        { 25, 8 },
        { 30, 9 }
    };

    public static readonly List<int> AvailableCosts = new List<int> { 2, 4, 6, 9, 12, 16, 20, 25, 30 };
}
