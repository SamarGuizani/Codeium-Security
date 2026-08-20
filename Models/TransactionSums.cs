namespace Codeium_Security.Models
{
    // Simple somme des montants d'un BankAccountSection. Aucune validation, aucune comparaison
    // avec le solde, aucune regle metier : juste SUM(Debit) et SUM(Credit) sur les transactions
    // deja extraites, sans jamais les modifier.
    public class TransactionSums
    {
        public decimal TotalDebit { get; set; }
        public decimal TotalCredit { get; set; }
    }
}
