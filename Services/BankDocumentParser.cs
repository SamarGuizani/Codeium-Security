using System.Globalization;
using System.Text.RegularExpressions;
using Codeium_Security.Models;

namespace Codeium_Security.Services
{
    public class BankDocumentParser
    {
        public BankDocument Parse(string text)
        {
            var document = new BankDocument();

            document.AccountNumber = ExtractAccountNumber(text);
            document.Currency = ExtractCurrency(text);
            document.Balance = ExtractBalance(text);
            document.CustomerName = ExtractCustomerName(text);
            document.BankName = ExtractBankName(text);

            return document;
        }

        private string ExtractAccountNumber(string text)
        {
            var match = Regex.Match(text, @"Compte\s*:?\s*([0-9A-Z\s]+?)(?=Relation|\\n|\n)");

            if (match.Success)
                return match.Groups[1].Value.Trim();

            return "";
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

            if (matches.Count == 0)
                return 0;

            var lastMatch = matches[matches.Count - 1];

            var cleaned = lastMatch.Value.Replace(" ", "").Replace(',', '.');

            decimal.TryParse(
                cleaned,
                NumberStyles.Any,
                CultureInfo.InvariantCulture,
                out decimal balance);

            return balance;
        }

        private string ExtractCustomerName(string text)
        {
            var match = Regex.Match(text, @"Relation\s*:?\s*(.+?)(?=Devise)");

            if (match.Success)
                return match.Groups[1].Value.Trim();

            return "";
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