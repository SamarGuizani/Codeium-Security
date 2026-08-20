namespace Codeium_Security.Models
{
    // Agrege les TransactionSums de tous les sous-comptes d'un BankDocument.
    public class DocumentTransactionSums
    {
        public List<TransactionSums> Accounts { get; set; } = new();

        public decimal TotalDebit => Accounts.Sum(a => a.TotalDebit);
        public decimal TotalCredit => Accounts.Sum(a => a.TotalCredit);
    }
}
