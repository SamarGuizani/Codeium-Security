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
            // "Compte" (français) OU "Account" (anglais, vu chez QNB)
            // "N°" ou "No" optionnel entre le mot et les chiffres (corrige WAFA)
            // Limité à 4 groupes de chiffres max (corrige BIAT qui avalait la date)
            // On prend le DERNIER trouvé, pas le premier (corrige QNB : 2 comptes dans le même doc)
            var matches = Regex.Matches(text,
                @"(?:Compte|Account)\s*(?:N[°o]?)?\s*(?:\(IBAN\))?\s*:?\s*([0-9]+(?:[\s\-/][0-9]+){0,3})",
                RegexOptions.IgnoreCase);
            if (matches.Count > 0)
                return Regex.Replace(matches[matches.Count - 1].Groups[1].Value.Trim(), @"\s{2,}", " ");

            var ribMatches = Regex.Matches(text, @"RIB\s*:?\s*([0-9]+(?:[\s][0-9]+){0,6})", RegexOptions.IgnoreCase);
            if (ribMatches.Count > 0)
                return ribMatches[ribMatches.Count - 1].Groups[1].Value.Trim();

            var ibanMatches = Regex.Matches(text, @"\b(\d{20})\b");
            return ibanMatches.Count > 0 ? ibanMatches[ibanMatches.Count - 1].Groups[1].Value : "";
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
            // Priorité 1 : "Solde Final" (format QNB)
            var soldeFinal = Regex.Matches(text,
                @"Solde\s*Final\s*:?\s*(-?\d{1,3}(?:[ .,]\d{3})*[.,]\d{2,3})",
                RegexOptions.IgnoreCase);
            if (soldeFinal.Count > 0)
                return ParseAmount(soldeFinal[soldeFinal.Count - 1].Groups[1].Value);

            // Priorité 2 : "Solde au JJ/MM/AAAA montant" (format WAFA)
            var soldeAu = Regex.Matches(text,
                @"Solde\s+au\s*:?\s*\d{2}[/\-.]\d{2}[/\-.]\d{4}\s*(-?\d{1,3}(?:[ .,]\d{3})*[.,]\d{2,3})",
                RegexOptions.IgnoreCase);
            if (soldeAu.Count > 0)
                return ParseAmount(soldeAu[soldeAu.Count - 1].Groups[1].Value);

            // Priorité 3 : dernier "Solde" générique dans le texte (BIAT, BNA...)
            var solde = Regex.Matches(text,
                @"Solde\s*(?:Créditeur|Débiteur)?\s*:?\s*(-?\d{1,3}(?:[ .,]\d{3})*[.,]\d{2,3})",
                RegexOptions.IgnoreCase);
            if (solde.Count > 0)
                return ParseAmount(solde[solde.Count - 1].Groups[1].Value);

            // Dernier recours : dernier montant du document, peu importe le contexte
            var matches = AmountRegex.Matches(text);
            return matches.Count > 0 ? ParseAmount(matches[matches.Count - 1].Value) : 0;
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