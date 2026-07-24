namespace Codeium_Security.Models
{
    public class BankDocument
    {
        public string AccountNumber { get; set; } = "";

        public string CustomerName { get; set; } = "";

        public decimal TotalDebit { get; set; }      // NOUVEAU
        public decimal TotalCredit { get; set; }

        public decimal SumOfDebits { get; set; }
        public decimal SumOfCredits { get; set; }
        public bool DebitTotalMatches { get; set; }
        public bool CreditTotalMatches { get; set; }
        public string BankName { get; set; } = "";

        public string Currency { get; set; } = "";

        public decimal Balance { get; set; }

        public DateTime? StatementDate { get; set; }

        public List<Transaction> Transactions { get; set; } = new();

        public decimal SoldeInitial { get; set; }


        public decimal SoldeDisponible { get; set; }
    }
}