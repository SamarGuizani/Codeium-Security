using System.Globalization;
using System.Text.RegularExpressions;
using Codeium_Security.Interfaces;
using Codeium_Security.Models;

namespace Codeium_Security.Services.DocumentParsers
{
    public class BankDocumentParser : IDocumentParser
    {
        public DocumentType SupportedType => DocumentType.Bank;

        private static readonly Regex DateRegex =
            new(@"\d{2}[/\-.]\d{2}[/\-.]\d{4}|\d{8}");

        private static readonly Regex AmountRegex =
            new(@"-?\d{1,3}(?:[ .,]\d{3})*[.,]\d{2,3}");

        public object Parse(string fullText, List<TextLine> lines)
        {
            var totalDebit = ExtractTotalDebit(fullText);
            var totalCredit = ExtractTotalCredit(fullText);

            if (totalDebit == 0 && totalCredit == 0)
            {
                var (debit, credit) = ExtractTotalMouvements(fullText);
                totalDebit = debit;
                totalCredit = credit;
            }

            var transactions = ExtractTransactions(lines);

            var document = new BankDocument
            {
                AccountNumber = ExtractAccountNumber(fullText),
                Currency = ExtractCurrency(fullText),
                Balance = ExtractBalance(fullText),
                SoldeInitial = ExtractSoldeInitial(fullText),
                SoldeDisponible = ExtractSoldeDisponible(fullText),
                TotalDebit = totalDebit,
                TotalCredit = totalCredit,
                CustomerName = ExtractCustomerName(fullText),
                BankName = ExtractBankName(fullText),
                Transactions = transactions
            };

            // BUG 16 : vérification totaux déclarés vs somme réelle des transactions extraites
            document.SumOfDebits = transactions.Where(t => t.Debit.HasValue).Sum(t => t.Debit!.Value);
            document.SumOfCredits = transactions.Where(t => t.Credit.HasValue).Sum(t => t.Credit!.Value);
            document.DebitTotalMatches = Math.Abs(document.SumOfDebits - Math.Abs(document.TotalDebit)) < 1m;
            document.CreditTotalMatches = Math.Abs(document.SumOfCredits - Math.Abs(document.TotalCredit)) < 1m;

            return document;
        }

        // Reconstruit les transactions à partir du tableau (lignes x colonnes) au lieu du texte plat.
        // Utilise DocumentAnalysisEngine.BuildTable, qui existait déjà mais n'était jamais appelée avant.
        private List<Transaction> ExtractTransactions(List<TextLine> lines)
        {
            var transactions = new List<Transaction>();
            var engine = new DocumentAnalysisEngine();
            var rows = engine.BuildTable(lines);
            decimal? previousSolde = null;

            foreach (var row in rows)
            {
                var cellTexts = row.Cells.Select(c => CleanWhitespace(c.Text)).ToList();
                if (cellTexts.Count == 0) continue;

                string joined = string.Join(" ", cellTexts);
                if (Regex.IsMatch(joined, @"\b(Total|Solde\s*Initial|Solde\s*Final|Page\s*\d)\b", RegexOptions.IgnoreCase))
                    continue;

                // Cellule 0 (la plus a gauche) = date d'operation attendue.
                // NormalizeDate retourne "" si la date n'est pas reelle -> la ligne est rejetee ici,
                // donc les en-tetes repetes / numeros de page / lignes sans date ne deviennent jamais des transactions.
                string normalizedDate = NormalizeDate(cellTexts[0].Trim());
                if (string.IsNullOrEmpty(normalizedDate)) continue;

                var amountCells = cellTexts.Skip(1)
                    .Where(c => AmountRegex.IsMatch(c))
                    .Select(c => ParseAmount(AmountRegex.Match(c).Value))
                    .ToList();

                if (amountCells.Count == 0) continue;

                string description = string.Join(" ", cellTexts.Skip(1).Where(c => !AmountRegex.IsMatch(c)))
                    .Trim(' ', '|', '[', ']', '-', '_');

                var tx = new Transaction { Date = normalizedDate, Description = description };

                if (amountCells.Count == 1)
                {
                    tx.Solde = amountCells[0];
                }
                else
                {
                    decimal mouvement = amountCells[0];
                    decimal solde = amountCells[amountCells.Count - 1];
                    tx.Solde = solde;

                    if (previousSolde.HasValue)
                    {
                        if (solde < previousSolde.Value) tx.Debit = mouvement;
                        else if (solde > previousSolde.Value) tx.Credit = mouvement;
                    }
                    else
                    {
                        tx.Debit = mouvement;
                    }
                }

                // BUG 15 : detecte CR/DB accole au solde, ex "1 320.062 CR"
                var crdb = Regex.Match(joined, @"\b(CR|DB)\b");
                if (crdb.Success) tx.SoldeType = crdb.Groups[1].Value;

                previousSolde = tx.Solde;
                transactions.Add(tx);
            }

            return transactions;
        }

        // BUG 5 : valide reellement la date (jour/mois/annee reels), rejette les dates impossibles.
        // Retourne "" si invalide -> c'est ce "" que ExtractTransactions verifie pour rejeter la ligne.
        private string NormalizeDate(string raw)
        {
            string candidate = raw;
            if (raw.Length == 8 && !raw.Contains('/') && !raw.Contains('-') && !raw.Contains('.'))
                candidate = $"{raw.Substring(0, 2)}/{raw.Substring(2, 2)}/{raw.Substring(4, 4)}";

            var formats = new[] { "dd/MM/yyyy", "dd-MM-yyyy", "dd.MM.yyyy" };
            if (DateTime.TryParseExact(candidate, formats, CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var parsed))
            {
                return parsed.ToString("dd/MM/yyyy");
            }

            return "";
        }

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
            string cleanText = Regex.Replace(text, @"\([^)]*\)", "");

            var dashMatches = Regex.Matches(cleanText, @"\b(\d{2,5}-\d{4,8}-\d{1,4})\b");
            if (dashMatches.Count > 0)
                return dashMatches[0].Groups[1].Value.Trim();

            var matches = Regex.Matches(cleanText,
                @"(?:Compte|Account)\s*(?:N[°o]?)?\s*:?\s*([0-9]+(?:[\s\-/][0-9]+){0,3})",
                RegexOptions.IgnoreCase);

            for (int i = matches.Count - 1; i >= 0; i--)
            {
                var candidate = Regex.Replace(matches[i].Groups[1].Value.Trim(), @"\s{2,}", " ");
                if (candidate.Replace(" ", "").Replace("-", "").Length >= 6
                    && !Regex.IsMatch(candidate, @"^\d{1,2}[\s/]\d{1,2}[\s/]\d{4}"))
                {
                    return candidate;
                }
            }

            var ribMatches = Regex.Matches(cleanText, @"RIB\s*:?\s*([0-9]+(?:[\s][0-9]+){0,6})", RegexOptions.IgnoreCase);
            if (ribMatches.Count > 0)
                return ribMatches[ribMatches.Count - 1].Groups[1].Value.Trim();

            // BUG 13 : ne plus deviner via un IBAN générique de 20 chiffres — retourne vide plutôt que d'inventer
            return "";
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
            var m = Regex.Matches(text,
                @"Total\s*(?:des\s*)?D[ée]bit(?:s)?\s*:?\s*(-?\d{1,3}(?:[ .,]\d{3})*[.,]\d{2,3})",
                RegexOptions.IgnoreCase);
            if (m.Count > 0)
                return ParseAmount(m[0].Groups[1].Value);
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