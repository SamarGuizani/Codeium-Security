namespace Codeium_Security.Frontend.Models;

public class TransactionViewModel
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public DateTime Date { get; set; }
    public string Libelle { get; set; } = string.Empty;
    public decimal? Debit { get; set; }
    public decimal? Credit { get; set; }
    public decimal Solde { get; set; }
}
