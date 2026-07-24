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
            var engine = new DocumentAnalysisEngine();
            var rows = engine.BuildTable(lines);

            var document = new BankDocument
            {
                CustomerName = ExtractCustomerName(fullText),
                BankName = ExtractBankName(fullText),
                Accounts = ExtractAccountSections(rows, fullText)
            };

            return document;
        }

        // Detecte chaque "Solde Initial" comme le debut d'un nouveau sous-compte,
        // et rattache toutes les transactions jusqu'au "Solde Final" suivant a ce sous-compte.
        // La logique interne de chaque transaction (debit/credit par comparaison de solde,
        // CR/DB, validation de date) est IDENTIQUE a l'ancienne ExtractTransactions, inchangee.
        private List<BankAccountSection> ExtractAccountSections(List<TableRow> rows, string fullText)
        {
            var sections = new List<BankAccountSection>();
            BankAccountSection? current = null;
            decimal? previousSolde = null;
            string lastSeenAccountNumber = "";

            foreach (var row in rows)
            {
                var cellTexts = row.Cells.Select(c => CleanWhitespace(c.Text)).ToList();
                if (cellTexts.Count == 0) continue;

                string joined = string.Join(" ", cellTexts);

                // Garde en memoire le dernier numero de compte au format avec tirets vu dans le document,
                // meme en dehors du tableau de transactions (ex: dans l'en-tete "Account (IBAN)")
                var accMatch = Regex.Match(joined, @"\b(\d{2,5}-\d{4,8}-\d{1,4})\b");
                if (accMatch.Success) lastSeenAccountNumber = accMatch.Groups[1].Value;

                // "SOLDE INITIAL" signale le debut d'un nouveau sous-compte -> on ferme la section
                // precedente (si elle existe) et on en ouvre une nouvelle avec sa propre reference de solde
                if (Regex.IsMatch(joined, @"Solde\s*Initial", RegexOptions.IgnoreCase))
                {
                    if (current != null) sections.Add(current);

                    var initMatch = AmountRegex.Match(joined);
                    decimal soldeInit = initMatch.Success ? ParseAmount(initMatch.Value) : 0;

                    current = new BankAccountSection
                    {
                        AccountNumber = lastSeenAccountNumber,
                        Currency = ExtractCurrency(fullText),
                        SoldeInitial = soldeInit
                    };
                    previousSolde = soldeInit;
                    continue;
                }

                if (Regex.IsMatch(joined, @"Solde\s*Final", RegexOptions.IgnoreCase))
                {
                    if (current != null)
                    {
                        var finalMatch = AmountRegex.Match(joined);
                        current.Balance = finalMatch.Success ? ParseAmount(finalMatch.Value) : (previousSolde ?? 0);
                    }
                    continue;
                }

                if (Regex.IsMatch(joined, @"\b(Total|Page\s*\d)\b", RegexOptions.IgnoreCase))
                    continue;

                if (current == null) continue; // lignes avant le premier "Solde Initial" (en-tetes generales)

                // ===== A PARTIR D'ICI : logique de transaction inchangee =====

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

                var crdb = Regex.Match(joined, @"\b(CR|DB)\b");
                if (crdb.Success) tx.SoldeType = crdb.Groups[1].Value;

                previousSolde = tx.Solde;
                current.Transactions.Add(tx);

                // ===== fin logique de transaction inchangee =====
            }

            if (current != null) sections.Add(current);

            // Totaux + verification par section (BUG 16, adapte a la structure par sous-compte)
            foreach (var sec in sections)
            {
                sec.SumOfDebits = sec.Transactions.Where(t => t.Debit.HasValue).Sum(t => t.Debit!.Value);
                sec.SumOfCredits = sec.Transactions.Where(t => t.Credit.HasValue).Sum(t => t.Credit!.Value);
                sec.DebitTotalMatches = Math.Abs(sec.SumOfDebits - Math.Abs(sec.TotalDebit)) < 1m;
                sec.CreditTotalMatches = Math.Abs(sec.SumOfCredits - Math.Abs(sec.TotalCredit)) < 1m;
            }

            return sections;
        }

        // BUG 5 : valide reellement la date (jour/mois/annee reels), rejette les dates impossibles.
        // Retourne "" si invalide -> c'est ce "" qui fait rejeter la ligne comme transaction.
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

        // Convertit "5.385,866" OU "1 500.01" en decimal correct,
        // en detectant automatiquement quel est le separateur decimal (le dernier)
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

        private string CleanWhitespace(string text) => text.Replace('\u00A0', ' ').Replace('\u202F', ' ');

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