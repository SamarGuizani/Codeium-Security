namespace Codeium_Security.Models
{
    public class BankDocument
    {
        public string AccountNumber { get; set; } = "";

        public string CustomerName { get; set; } = "";

        public string BankName { get; set; } = "";

        public string Currency { get; set; } = "";

        public decimal Balance { get; set; }

        public DateTime? StatementDate { get; set; }
    }
}