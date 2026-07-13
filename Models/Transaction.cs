namespace Codeium_Security.Models
{
    public class Transaction
    {
        public string Date { get; set; } = "";
        public string Description { get; set; } = "";
        public decimal? Debit { get; set; }

        public decimal? Credit { get; set; } 
        public decimal? BalanceAfterOperation { get; set; }
    }
}