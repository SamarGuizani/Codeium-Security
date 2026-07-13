namespace Codeium_Security.Models
{
    public class InvoiceDocument
    {

        public string InvoiceNumber { get; set; } = "";
        public string SupplierName { get; set; } = "";
        public DateTime? InvoiceDate { get; set; }
         public decimal TotalHT { get; set; }

        public decimal TotalTTC { get; set; }
        public decimal Tva { get; set; }

        public string Currency { get; set; } = "";
    }
}
