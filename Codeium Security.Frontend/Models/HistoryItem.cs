namespace Codeium_Security.Frontend.Models;

public class HistoryItem
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string NomDocument { get; set; } = string.Empty;
    public string Banque { get; set; } = string.Empty;
    public DateTime Date { get; set; }
    public ProcessingStatus Statut { get; set; }
    public int NombreTransactions { get; set; }
}
