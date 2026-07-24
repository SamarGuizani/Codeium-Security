namespace Codeium_Security.Models
{
    public class Transaction
    {
        public decimal? Solde { get; set; }
        public string Date { get; set; } = "";
        public string Description { get; set; } = "";

        public string? SoldeType { get; set; } // "CR" ou "DB"
        public decimal? Debit { get; set; }

        public decimal? Credit { get; set; }
        
        public decimal? BalanceAfterOperation { get; set; }
    }
}