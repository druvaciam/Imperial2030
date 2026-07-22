namespace Imperial2030.Shared.Constants;

public static class TaxChart
{
    public static int GetPowerGain(int taxRevenue)
    {
        if (taxRevenue <= 5) return 0;
        if (taxRevenue <= 7) return 1;
        if (taxRevenue <= 9) return 2;
        if (taxRevenue == 10) return 3;
        if (taxRevenue == 11) return 4;
        if (taxRevenue == 12) return 5;
        if (taxRevenue == 13) return 6;
        if (taxRevenue == 14) return 7;
        if (taxRevenue == 15) return 8;
        if (taxRevenue <= 17) return 9;
        return 10;
    }

    public static int GetStandardBonus(int taxRevenue)
    {
        if (taxRevenue >= 16) return 5;
        if (taxRevenue >= 14) return 4;
        if (taxRevenue >= 12) return 3;
        if (taxRevenue >= 10) return 2;
        if (taxRevenue >= 6) return 1;
        return 0;
    }
}
