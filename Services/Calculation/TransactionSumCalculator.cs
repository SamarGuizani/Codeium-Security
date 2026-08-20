using Codeium_Security.Models;

namespace Codeium_Security.Services.Calculation
{
    // Calcule uniquement SUM(Debit) et SUM(Credit) sur les transactions deja extraites par
    // BankDocumentParser, pour toutes les banques (aucune logique specifique a une banque ici).
    // Ne fait AUCUNE validation, AUCUNE comparaison avec le solde, AUCUNE regle metier, et ne
    // modifie jamais les transactions. Sans dependance (pas de constructeur a parametres) pour
    // pouvoir etre instancie librement (`new TransactionSumCalculator()`) partout ou necessaire.
    public class TransactionSumCalculator
    {
        public TransactionSums Calculate(BankAccountSection account)
        {
            decimal totalDebit = 0m;
            decimal totalCredit = 0m;

            foreach (var tx in account.Transactions)
            {
                if (tx.Debit.HasValue) totalDebit += tx.Debit.Value;
                if (tx.Credit.HasValue) totalCredit += tx.Credit.Value;
            }

            return new TransactionSums { TotalDebit = totalDebit, TotalCredit = totalCredit };
        }

        public DocumentTransactionSums Calculate(BankDocument document)
        {
            return new DocumentTransactionSums
            {
                Accounts = document.Accounts.Select(Calculate).ToList()
            };
        }
    }
}
