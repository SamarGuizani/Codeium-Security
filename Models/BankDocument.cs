namespace Codeium_Security.Models
{
    public class BankDocument
    {
        public decimal? TotalDebit { get; set; }
        public decimal? TotalCredit { get; set; }
        public string BankName { get; set; } = "";
        public List<BankAccountSection> Accounts { get; set; } = new();
    }
}