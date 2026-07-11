using System.Globalization;
using System.Text.RegularExpressions;
using Codeium_Security.Models;

namespace Codeium_Security.Services
{
    public class BankDocumentParser
    {
        public BankDocument Parse(string text, List<TextLine>? lines = null)
        {
            var document = new BankDocument();

            document.AccountNumber = ExtractAccountNumber(text);
            document.Currency = ExtractCurrency(text);
            document.Balance = ExtractBalance(text);
            document.CustomerName = ExtractCustomerName(text);
            document.BankName = ExtractBankName(text);

            if (lines != null)
                document.Transactions = ExtractTransactions(lines);

            return document;
        }

        private List<Transaction> ExtractTransactions(List<TextLine> lines)
        {
            var transactions = new List<Transaction>();
            var dateRegex = new Regex(@"\d{2}/\d{2}/\d{4}");

            // Regex STRICTE : chiffres (1-3), puis groupes EXACTS de 3 chiffres séparés
            // par un seul espace, puis un séparateur décimal et 2-3 décimales.
            // Ça empêche d'avaler des numéros de chèque ou des dates par erreur.
            var numberRegex = new Regex(@"-?\d{1,3}(?:\s\d{3})*[.,]\d{2,3}");

            foreach (var line in lines)
            {
                var lineText = line.FullLineText;

                var dateMatches = dateRegex.Matches(lineText);
                if (dateMatches.Count == 0) continue;

                var numberMatches = numberRegex.Matches(lineText);
                if (numberMatches.Count == 0) continue;

                var firstDate = dateMatches[0];

                int descStart = firstDate.Index + firstDate.Length;
                int descEnd = numberMatches[0].Index;

                string description = descEnd > descStart
                    ? lineText.Substring(descStart, descEnd - descStart)
                    : "";

                // On retire une éventuelle 2ème date (date de valeur) qui traînerait
                // dans le texte de la description, puis on nettoie les symboles parasites
                description = dateRegex.Replace(description, "").Trim(' ', '|', '[', ']', '-', '_');

                var tx = new Transaction
                {
                    Date = firstDate.Value,
                    Description = description
                };

                var cleanedNumbers = numberMatches
                    .Select(m => m.Value.Replace(" ", "").Replace(',', '.'))
                    .ToList();

                if (cleanedNumbers.Count >= 1)
                {
                    decimal.TryParse(cleanedNumbers[0], NumberStyles.Any, CultureInfo.InvariantCulture, out var amount);
                    tx.Debit = amount;
                }

                if (cleanedNumbers.Count >= 2)
                {
                    decimal.TryParse(cleanedNumbers[^1], NumberStyles.Any, CultureInfo.InvariantCulture, out var balance);
                    tx.BalanceAfterOperation = balance;
                }

                transactions.Add(tx);
            }

            return transactions;
        }

        private string ExtractAccountNumber(string text)
        {
            var match = Regex.Match(text, @"Compte\s*:?\s*([0-9A-Z\s]+?)(?=Relation|\\n|\n)");
            return match.Success ? match.Groups[1].Value.Trim() : "";
        }

        private string ExtractCurrency(string text)
        {
            if (text.Contains("TND")) return "TND";
            if (text.Contains("DINAR")) return "TND";
            if (text.Contains("EUR")) return "EUR";
            if (text.Contains("USD")) return "USD";
            return "";
        }

        private decimal ExtractBalance(string text)
        {
            var matches = Regex.Matches(text, @"-?\d[\d\s]*[.,]\d{2,3}");
            if (matches.Count == 0) return 0;

            var lastMatch = matches[matches.Count - 1];
            var cleaned = lastMatch.Value.Replace(" ", "").Replace(',', '.');

            decimal.TryParse(cleaned, NumberStyles.Any, CultureInfo.InvariantCulture, out decimal balance);
            return balance;
        }

        private string ExtractCustomerName(string text)
        {
            var match = Regex.Match(text, @"Relation\s*:?\s*(.+?)(?=Devise)");
            return match.Success ? match.Groups[1].Value.Trim() : "";
        }

        private string ExtractBankName(string text)
        {
            var knownBanks = new (string Keyword, string FullName)[]
            {
                ("BNA", "Banque Nationale Agricole (BNA)"),
                ("BIAT", "Banque Internationale Arabe de Tunisie (BIAT)"),
                ("STB", "Société Tunisienne de Banque (STB)"),
                ("ATB", "Arab Tunisian Bank (ATB)"),
                ("UIB", "Union Internationale de Banques (UIB)"),
                ("ATTIJARI", "Attijari Bank"),
                ("BH", "Banque de l'Habitat (BH)")
            };

            foreach (var bank in knownBanks)
            {
                if (text.Contains(bank.Keyword))
                    return bank.FullName;
            }

            return "";
        }
    }
}