namespace Codeium_Security.Models
{
    public class ReceiptDocument
    {

        public string StoreName { get; set; } = "";
         public DateTime? Date { get; set; }
        public decimal Total { get; set; }
        public string Currency { get; set; } = "";

    }
}
