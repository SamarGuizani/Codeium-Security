using System.Globalization;
using System.Text.RegularExpressions;
using Codeium_Security.Interfaces;
using Codeium_Security.Models;

namespace Codeium_Security.Services.DocumentParsers
{
    public class BankDocumentParser : IDocumentParser
    {
        public DocumentType SupportedType => DocumentType.Bank;

        // Accepte JJ/MM/AAAA (BNA) OU JJMMAAAA collé (BIAT)
        private static readonly Regex DateRegex =
            new(@"\d{2}[/\-.]\d{2}[/\-.]\d{4}|\d{8}");

        // Accepte les montants avec séparateur milliers en espace, point ou virgule
        private static readonly Regex AmountRegex =
            new(@"-?\d{1,3}(?:[ .,]\d{3})*[.,]\d{2,3}");

        public object Parse(string fullText, List<TextLine> lines)
        {
            var document = new BankDocument
            {
                AccountNumber = ExtractAccountNumber(fullText),
                Currency = ExtractCurrency(fullText),
                Balance = ExtractBalance(fullText),
                CustomerName = ExtractCustomerName(fullText),
                BankName = ExtractBankName(fullText),
                Transactions = ExtractTransactions(lines)
            };

            return document;
        }

        private List<Transaction> ExtractTransactions(List<TextLine> lines)
        {
            var transactions = new List<Transaction>();

            foreach (var line in lines)
            {
                var lineText = line.FullLineText;
                if (Regex.IsMatch(lineText, @"\b(Solde|Total)\b", RegexOptions.IgnoreCase))
                    continue;

                var dateMatches = DateRegex.Matches(lineText);
                if (dateMatches.Count == 0) continue;

                // On prend la DERNIÈRE date de la ligne (= date de valeur, la plus fiable)
                var dateMatch = dateMatches[dateMatches.Count - 1];

                string remainder = lineText.Substring(dateMatch.Index + dateMatch.Length);
                var amountMatches = AmountRegex.Matches(remainder);
                if (amountMatches.Count == 0) continue;

                string description = lineText.Substring(0, dateMatch.Index)
                    .Trim(' ', '|', '[', ']', '-', '_');

                // Retire un numéro jour/mois isolé en début de description (ex: "03 01 ")
                description = Regex.Replace(description, @"^\d{1,2}\s?\d{0,2}\s*", "").Trim();

                var tx = new Transaction
                {
                    Date = NormalizeDate(dateMatch.Value),
                    Description = description
                };

                var amounts = amountMatches.Select(m => ParseAmount(m.Value)).ToList();

                if (amounts.Count >= 1) tx.Debit = amounts[0];
                if (amounts.Count >= 2) tx.Credit = amounts[1];

                transactions.Add(tx);
            }

            return transactions;
        }

        // Transforme "04012023" en "04/01/2023" ; laisse "01/07/2025" tel quel
        private string NormalizeDate(string raw)
        {
            if (raw.Length == 8 && !raw.Contains('/') && !raw.Contains('-') && !raw.Contains('.'))
                return $"{raw.Substring(0, 2)}/{raw.Substring(2, 2)}/{raw.Substring(4, 4)}";

            return raw;
        }

        // Convertit "5.385,866" OU "1 500.01" en decimal correct,
        // en détectant automatiquement quel est le séparateur décimal (le dernier)
        private decimal ParseAmount(string raw)
        {
            int lastSepIndex = -1;
            for (int i = raw.Length - 1; i >= 0; i--)
            {
                if (raw[i] == '.' || raw[i] == ',' || raw[i] == ' ')
                {
                    lastSepIndex = i;
                    break;
                }
            }

            if (lastSepIndex == -1)
            {
                decimal.TryParse(raw, NumberStyles.Any, CultureInfo.InvariantCulture, out var direct);
                return direct;
            }

            string integerPart = raw.Substring(0, lastSepIndex).Replace(".", "").Replace(",", "").Replace(" ", "");
            string decimalPart = raw.Substring(lastSepIndex + 1);

            decimal.TryParse(integerPart + "." + decimalPart, NumberStyles.Any, CultureInfo.InvariantCulture, out var result);
            return result;
        }  
        private string ExtractAccountNumber(string text)
        {
            var match = Regex.Match(text, @"Compte\s*:?\s*([0-9A-Z\s]+?)(?=Relation|\\n|\n)");
            if (match.Success) return match.Groups[1].Value.Trim();

            // NOUVEAU : format "Numéro de compte : 00010-0082693425-5" (Attijari, avec tirets)
            var numeroMatch = Regex.Match(text, @"compte\s*:?\s*([0-9][0-9A-Z\-]+)", RegexOptions.IgnoreCase);
            if (numeroMatch.Success) return numeroMatch.Groups[1].Value.Trim();

            // Format RIB (utilisé par BIAT) : "RIB : 08 307 00059 10 02049 0 36"
            var ribMatch = Regex.Match(text, @"RIB\s*:?\s*([0-9\s]+)");
            return ribMatch.Success ? ribMatch.Groups[1].Value.Trim() : "";
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
            var matches = AmountRegex.Matches(text);
            if (matches.Count == 0) return 0;

            return ParseAmount(matches[matches.Count - 1].Value);
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
                if (text.Contains(bank.Keyword))
                    return bank.FullName;

            return "";
        }
    }
}