namespace Imperial2030.Shared.Models
{
    public class InvestorMetadata
    {
        public string? Type { get; set; } // e.g. "InterestPaid", "InterestPartial", "TreasuryEmpty", "PersonallyContributed", "UnableToPay", "InvestorBonus"
        public int? PaidAmount { get; set; }
        public int? ExpectedAmount { get; set; }
        public string? PayeeName { get; set; }
        public int? PersonalContribution { get; set; }
        public bool? MissedInterest { get; set; }
        public bool? TreasuryEmpty { get; set; }
    }
}
