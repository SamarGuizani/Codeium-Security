namespace Codeium_Security.Models
{
    public class Transaction
    {
        public decimal? Solde { get; set; }
        public string Date { get; set; } = "";
        public string Libelle { get; set; } = "";
        public decimal? Debit { get; set; }

        public decimal? Credit { get; set; }
       
    }
}