namespace Codeium_Security.Frontend.Models;

public class BankResultViewModel
{
    public string Banque { get; set; } = string.Empty;
    public string NumeroCompteMasque { get; set; } = string.Empty;
    public string Periode { get; set; } = string.Empty;
    public ProcessingStatus Statut { get; set; } = ProcessingStatus.Termine;
    public List<TransactionViewModel> Transactions { get; set; } = new();

    public int NombreTransactions => Transactions.Count;
}
