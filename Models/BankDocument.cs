namespace Codeium_Security.Models
{
    public class BankDocument
    {
        public string CustomerName { get; set; } = "";
        public string BankName { get; set; } = "";
        public List<BankAccountSection> Accounts { get; set; } = new();
    }
}