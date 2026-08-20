using Codeium_Security.Models;
using Codeium_Security.Services.Calculation;
using Xunit;

namespace Codeium_Security.Tests
{
    // Verifie TransactionSumCalculator isolement (donnees synthetiques, aucun pipeline OCR) :
    // uniquement SUM(Debit) et SUM(Credit), sans validation, sans comparaison avec le solde,
    // sans regle metier. Generique par construction (aucune reference a une banque particuliere).
    public class TransactionSumCalculatorTests
    {
        [Fact]
        public void Sums_Debit_And_Credit_Independently()
        {
            var account = new BankAccountSection
            {
                Transactions = new List<Transaction>
                {
                    new() { Date = "01/01/2025", Libelle = "A", Debit = 100.500m },
                    new() { Date = "02/01/2025", Libelle = "B", Credit = 50.250m },
                    new() { Date = "03/01/2025", Libelle = "C", Debit = 10.000m },
                    new() { Date = "04/01/2025", Libelle = "D", Credit = 5.000m },
                }
            };

            var sums = new TransactionSumCalculator().Calculate(account);

            Assert.Equal(110.500m, sums.TotalDebit);
            Assert.Equal(55.250m, sums.TotalCredit);
        }

        [Fact]
        public void Treats_Missing_Amounts_As_Zero()
        {
            var account = new BankAccountSection
            {
                Transactions = new List<Transaction>
                {
                    new() { Date = "01/01/2025", Libelle = "Sans montant" },
                    new() { Date = "02/01/2025", Libelle = "Debit seul", Debit = 20m },
                }
            };

            var sums = new TransactionSumCalculator().Calculate(account);

            Assert.Equal(20m, sums.TotalDebit);
            Assert.Equal(0m, sums.TotalCredit);
        }

        [Fact]
        public void Empty_Account_Returns_Zero_Sums()
        {
            var sums = new TransactionSumCalculator().Calculate(new BankAccountSection());

            Assert.Equal(0m, sums.TotalDebit);
            Assert.Equal(0m, sums.TotalCredit);
        }

        [Fact]
        public void Does_Not_Modify_Transactions()
        {
            var tx = new Transaction { Date = "01/01/2025", Libelle = "A", Debit = 100m, Credit = null };
            var account = new BankAccountSection { Transactions = new List<Transaction> { tx } };

            new TransactionSumCalculator().Calculate(account);

            Assert.Equal(100m, tx.Debit);
            Assert.Null(tx.Credit);
            Assert.Equal("A", tx.Libelle);
        }

        [Fact]
        public void Document_Level_Sums_Aggregate_All_Accounts()
        {
            var document = new BankDocument
            {
                BankName = "Banque Test",
                Accounts = new List<BankAccountSection>
                {
                    new() { Transactions = new List<Transaction> { new() { Debit = 10m } } },
                    new() { Transactions = new List<Transaction> { new() { Credit = 25m } } },
                }
            };

            var sums = new TransactionSumCalculator().Calculate(document);

            Assert.Equal(2, sums.Accounts.Count);
            Assert.Equal(10m, sums.TotalDebit);
            Assert.Equal(25m, sums.TotalCredit);
        }
    }
}
