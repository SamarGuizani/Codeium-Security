namespace Codeium_Security.Models
{
    public class BankAccountSection
    {
        public string AccountNumber { get; set; } = "";
        public string Currency { get; set; } = "";
        public decimal Balance { get; set; }
        public decimal SoldeInitial { get; set; }
        public decimal SoldeDisponible { get; set; }
        public decimal TotalDebit { get; set; }
        public decimal TotalCredit { get; set; }
        public decimal SumOfDebits { get; set; }
        public decimal SumOfCredits { get; set; }
        public bool DebitTotalMatches { get; set; }
        public bool CreditTotalMatches { get; set; }
        public List<Transaction> Transactions { get; set; } = new();
        [System.Text.Json.Serialization.JsonIgnore]
        public string RawSectionText { get; set; } = "";
    }
}