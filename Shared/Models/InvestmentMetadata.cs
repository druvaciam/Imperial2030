namespace Imperial2030.Shared.Models
{
    public class InvestmentMetadata
    {
        public string? NewControllerName { get; set; }
        public string? OldControllerName { get; set; }
        public bool IsSwissBankKicked { get; set; }
        public string? Nation { get; set; }
        public int? Cost { get; set; }
        public int? TradeInCost { get; set; }
    }
}
