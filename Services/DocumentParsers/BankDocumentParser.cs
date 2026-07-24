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
            var totalDebit = ExtractTotalDebit(fullText);
            var totalCredit = ExtractTotalCredit(fullText);

            // Si aucun mot-clé "Total Débit/Crédit" trouvé, essaie le format "Total des mouvements"
            if (totalDebit == 0 && totalCredit == 0)
            {
                var (debit, credit) = ExtractTotalMouvements(fullText);
                totalDebit = debit;
                totalCredit = credit;
            }

            var document = new BankDocument
            {
                AccountNumber = ExtractAccountNumber(fullText),
                Currency = ExtractCurrency(fullText),
                Balance = ExtractBalance(fullText),
                SoldeInitial = ExtractSoldeInitial(fullText),
                SoldeDisponible = ExtractSoldeDisponible(fullText),
                TotalDebit = totalDebit,           // NOUVEAU
                TotalCredit = totalCredit,         // NOUVEAU
                CustomerName = ExtractCustomerName(fullText),
                BankName = ExtractBankName(fullText),
                Transactions = ExtractTransactions(lines)
            };

            return document;
        }

        private List<Transaction> ExtractTransactions(List<TextLine> lines)
        {
            var transactions = new List<Transaction>();
            decimal? previousSolde = null;

            foreach (var line in lines)
            {
                var lineText = CleanWhitespace(line.FullLineText);

                if (Regex.IsMatch(lineText, @"\b(Total|Solde\s*Initial|Solde\s*Final)\b", RegexOptions.IgnoreCase))
                    continue;

                var dateMatches = DateRegex.Matches(lineText);
                if (dateMatches.Count == 0) continue;

                var dateMatch = dateMatches[dateMatches.Count - 1];
                string remainder = lineText.Substring(dateMatch.Index + dateMatch.Length);
                var amountMatches = AmountRegex.Matches(remainder);
                if (amountMatches.Count == 0) continue;

                string description = lineText.Substring(0, dateMatch.Index)
                    .Trim(' ', '|', '[', ']', '-', '_');
                description = Regex.Replace(description, @"^\d{1,2}\s?\d{0,2}\s*", "").Trim();

                var amounts = amountMatches.Select(m => ParseAmount(m.Value)).ToList();

                var tx = new Transaction { Date = NormalizeDate(dateMatch.Value), Description = description };

                if (amounts.Count == 1)
                {
                    // Une seule valeur trouvée = probablement juste le solde (pas de mouvement ce jour-là)
                    tx.Solde = amounts[0];
                }
                else
                {
                    // 2 valeurs (ou plus) : la DERNIÈRE est le solde, la première est le montant du mouvement
                    decimal mouvement = amounts[0];
                    decimal solde = amounts[amounts.Count - 1];
                    tx.Solde = solde;

                    if (previousSolde.HasValue)
                    {
                        // Débit si le solde a baissé, Crédit si le solde a monté
                        if (solde < previousSolde.Value) tx.Debit = mouvement;
                        else if (solde > previousSolde.Value) tx.Credit = mouvement;
                    }
                    else
                    {
                        // Pas de solde précédent connu (première ligne) : on ne peut pas deviner, on met en Débit par défaut
                        tx.Debit = mouvement;
                    }
                }

                previousSolde = tx.Solde;
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
            // Étape 0 : on retire tout ce qui est entre parenthèses (souvent un IBAN dupliqué du même compte)
            string cleanText = Regex.Replace(text, @"\([^)]*\)", "");

            // Priorité 1 : format avec tirets, ex: "2019-112548-001", "00010-0082693425-5"
            var dashMatches = Regex.Matches(cleanText, @"\b(\d{2,5}-\d{4,8}-\d{1,4})\b");
            if (dashMatches.Count > 0)
                return dashMatches[0].Groups[1].Value.Trim();

            // Priorité 2 : "Compte" / "Account" suivi de chiffres, en rejetant tout match qui ressemble à une date
            var matches = Regex.Matches(cleanText,
                @"(?:Compte|Account)\s*(?:N[°o]?)?\s*:?\s*([0-9]+(?:[\s\-/][0-9]+){0,3})",
                RegexOptions.IgnoreCase);

            for (int i = matches.Count - 1; i >= 0; i--)
            {
                var candidate = Regex.Replace(matches[i].Groups[1].Value.Trim(), @"\s{2,}", " ");
                // Rejette un candidat trop court (souvent une date ou une page comme "1", "2016")
                // ou qui ressemble a une date JJ/MM/AAAA
                if (candidate.Replace(" ", "").Replace("-", "").Length >= 6
                    && !Regex.IsMatch(candidate, @"^\d{1,2}[\s/]\d{1,2}[\s/]\d{4}"))
                {
                    return candidate;
                }
            }

            var ribMatches = Regex.Matches(cleanText, @"RIB\s*:?\s*([0-9]+(?:[\s][0-9]+){0,6})", RegexOptions.IgnoreCase);
            if (ribMatches.Count > 0)
                return ribMatches[ribMatches.Count - 1].Groups[1].Value.Trim();

            var ibanMatches = Regex.Matches(cleanText, @"\b(\d{20})\b");
            return ibanMatches.Count > 0 ? ibanMatches[ibanMatches.Count - 1].Groups[1].Value : "";
        }
        private string CleanWhitespace(string text) => text.Replace('\u00A0', ' ').Replace('\u202F', ' ');
        private decimal ExtractSoldeInitial(string text)
        {
            text = CleanWhitespace(text);
            var m = Regex.Matches(text,
                @"Solde\s*Initial\s*:?\s*(-?\d{1,3}(?:[ .,]\d{3})*[.,]\d{2,3})",
                RegexOptions.IgnoreCase);
            return m.Count > 0 ? ParseAmount(m[0].Groups[1].Value) : 0;
        }
        private decimal ExtractBalance(string text)
        {
            text = CleanWhitespace(text);

            var soldeFinal = Regex.Matches(text,
                @"Solde\s*Final\s*:?\s*(-?\d{1,3}(?:[ .,]\d{3})*[.,]\d{2,3})",
                RegexOptions.IgnoreCase);
            if (soldeFinal.Count > 0)
                return ParseAmount(soldeFinal[soldeFinal.Count - 1].Groups[1].Value);

            var soldeAu = Regex.Matches(text,
                @"Solde\s+au\s*:?\s*\d{2}[/\-.]\d{2}[/\-.]\d{4}\s*(-?\d{1,3}(?:[ .,]\d{3})*[.,]\d{2,3})",
                RegexOptions.IgnoreCase);
            if (soldeAu.Count > 0)
                return ParseAmount(soldeAu[soldeAu.Count - 1].Groups[1].Value);

            var solde = Regex.Matches(text,
                @"Solde\s*(?:Créditeur|Débiteur)?\s*:?\s*(-?\d{1,3}(?:[ .,]\d{3})*[.,]\d{2,3})",
                RegexOptions.IgnoreCase);
            if (solde.Count > 0)
                return ParseAmount(solde[solde.Count - 1].Groups[1].Value);

            var matches = AmountRegex.Matches(text);
            return matches.Count > 0 ? ParseAmount(matches[matches.Count - 1].Value) : 0;
        }
        private decimal ExtractSoldeDisponible(string text)
        {
            text = CleanWhitespace(text);
            var m = Regex.Matches(text,
                @"Solde\s*Disponible\s*:?\s*(-?\d{1,3}(?:[ .,]\d{3})*[.,]\d{2,3})",
                RegexOptions.IgnoreCase);
            return m.Count > 0 ? ParseAmount(m[m.Count - 1].Groups[1].Value) : 0;
        }
        private decimal ExtractTotalDebit(string text)
        {
            text = CleanWhitespace(text);

            // Cherche "Total Débit" ou "Total des Débits" (accepte accent ou pas)
            var m = Regex.Matches(text,
                @"Total\s*(?:des\s*)?D[ée]bit(?:s)?\s*:?\s*(-?\d{1,3}(?:[ .,]\d{3})*[.,]\d{2,3})",
                RegexOptions.IgnoreCase);
            if (m.Count > 0)
                return ParseAmount(m[0].Groups[1].Value);

            // Repli : additionne tous les débits de chaque transaction si aucun total explicite trouvé
            return 0;
        }

        private decimal ExtractTotalCredit(string text)
        {
            text = CleanWhitespace(text);

            var m = Regex.Matches(text,
                @"Total\s*(?:des\s*)?Cr[ée]dit(?:s)?\s*:?\s*(-?\d{1,3}(?:[ .,]\d{3})*[.,]\d{2,3})",
                RegexOptions.IgnoreCase);
            if (m.Count > 0)
                return ParseAmount(m[0].Groups[1].Value);

            return 0;
        }
        private (decimal debit, decimal credit) ExtractTotalMouvements(string text)
        {
            text = CleanWhitespace(text);

            var m = Regex.Match(text,
                @"Total\s*des\s*mouvements\s*:?\s*(-?\d{1,3}(?:[ .,]\d{3})*[.,]\d{2,3})\s+(-?\d{1,3}(?:[ .,]\d{3})*[.,]\d{2,3})",
                RegexOptions.IgnoreCase);

            if (m.Success)
                return (ParseAmount(m.Groups[1].Value), ParseAmount(m.Groups[2].Value));

            return (0, 0);
        }


        private string ExtractCurrency(string text)
        {
            if (text.Contains("TND")) return "TND";
            if (text.Contains("DINAR")) return "TND";
            if (text.Contains("EUR")) return "EUR";
            if (text.Contains("USD")) return "USD";
            return "";
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