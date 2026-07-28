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
            string sectionRawText = "";

            // Calibration des colonnes par en-tete (position X de "Debit"/"Credit"/"Solde")
            int? debitAnchor = null, creditAnchor = null, soldeAnchor = null;

            foreach (var row in rows)
            {
                var cells = row.Cells;
                var cellTextsRaw = cells.Select(c => CleanWhitespace(c.Text)).ToList();
                var cellTexts = MergeLoneSignCells(cellTextsRaw);
                if (cellTexts.Count == 0) continue;

                string joined = string.Join(" ", cellTexts);
                sectionRawText += joined + "\n";

                var accMatch = Regex.Match(joined, @"\b(\d{2,5}-\d{4,8}-\d{1,4})\b");
                if (accMatch.Success) lastSeenAccountNumber = accMatch.Groups[1].Value;

             

                var ribMatch = Regex.Match(joined, @"TN\d{2}[\s\d]{15,25}");
                if (ribMatch.Success && current != null) current.Rib = Regex.Replace(ribMatch.Value, @"\s+", " ").Trim();

                // Detecte la ligne d'en-tete de colonnes et calibre les positions X une fois par section
                var debitCell = cells.FirstOrDefault(c => Regex.IsMatch(c.Text, @"D[ée]bit", RegexOptions.IgnoreCase));
                var creditCell = cells.FirstOrDefault(c => Regex.IsMatch(c.Text, @"Cr[ée]dit", RegexOptions.IgnoreCase));
                var soldeCell = cells.FirstOrDefault(c => Regex.IsMatch(c.Text, @"^Solde$", RegexOptions.IgnoreCase));
                if (debitCell != null || creditCell != null)
                {
                    if (debitCell != null) debitAnchor = debitCell.Left;
                    if (creditCell != null) creditAnchor = creditCell.Left;
                    if (soldeCell != null) soldeAnchor = soldeCell.Left;
                    continue; // ligne d'en-tete, jamais une transaction
                }

                if (Regex.IsMatch(joined, @"Solde\s*Initial", RegexOptions.IgnoreCase))
                {
                    if (current != null)
                    {
                        current.RawSectionText = sectionRawText;
                        sections.Add(current);
                    }
                    sectionRawText = joined + "\n";

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
                        current.SoldeFinal = finalMatch.Success ? ParseAmount(finalMatch.Value) : previousSolde;
                    }
                    continue;
                }

                if (Regex.IsMatch(joined, @"\b(Total|Page\s*\d)\b", RegexOptions.IgnoreCase))
                    continue;

                if (current == null) continue;

                string normalizedDate = NormalizeDate(cellTexts[0].Trim());

                if (string.IsNullOrEmpty(normalizedDate))
                {
                    // Pas de date sur cette ligne : c'est la suite du libelle de la derniere transaction
                    // (reference, POS, nom du beneficiaire, motif, etc. - tout ce qui suit avant la prochaine date)
                    if (current.Transactions.Count > 0 && !AmountRegex.IsMatch(joined)
                        && !Regex.IsMatch(joined, @"\b(Total|Page\s*\d|Solde\s*(Initial|Final))\b", RegexOptions.IgnoreCase))
                    {
                        var lastTx = current.Transactions[current.Transactions.Count - 1];
                        lastTx.Libelle = (lastTx.Libelle + " " + joined.Trim()).Trim();
                    }
                    continue;
                }
                if (string.IsNullOrEmpty(normalizedDate)) continue;

                // Cellules candidates montant, AVEC leur position X d'origine
                var amountCandidates = cells.Skip(0)
                    .Where(c => AmountRegex.IsMatch(MergeLoneSignCells(new List<string> { CleanWhitespace(c.Text) })[0]))
                    .Select(c => new { Left = c.Left, Value = ParseAmount(AmountRegex.Match(CleanWhitespace(c.Text)).Value) })
                    .ToList();

                if (amountCandidates.Count == 0) continue;

                string description = string.Join(" ", cellTexts.Skip(1).Where(c => !AmountRegex.IsMatch(c)))
                    .Trim(' ', '|', '[', ']', '-', '_');
                // Filet de securite : retire toute date qui aurait fuite dans le libelle
                description = Regex.Replace(description, @"\b\d{2}[/\-.]\d{2}[/\-.]\d{4}\b", "").Trim();

                var tx = new Transaction { Date = normalizedDate, Libelle = description };

                if (debitAnchor.HasValue && creditAnchor.HasValue)
                {
                    // Assignation par vraie position de colonne, pas par devinette
                    foreach (var cand in amountCandidates)
                    {
                        int distDebit = Math.Abs(cand.Left - debitAnchor.Value);
                        int distCredit = Math.Abs(cand.Left - creditAnchor.Value);
                        int distSolde = soldeAnchor.HasValue ? Math.Abs(cand.Left - soldeAnchor.Value) : int.MaxValue;

                        if (distSolde <= distDebit && distSolde <= distCredit)
                            tx.Solde = cand.Value;
                        else if (distDebit < distCredit)
                            tx.Debit = cand.Value;
                        else
                            tx.Credit = cand.Value;
                    }
                    if (!tx.Solde.HasValue && amountCandidates.Count > 0)
                        tx.Solde = amountCandidates[amountCandidates.Count - 1].Value;
                }
                else
                {
                    // Repli : pas d'en-tete detecte pour cette section -> ancienne heuristique par comparaison de solde
                    var amounts = amountCandidates.Select(a => a.Value).ToList();
                    if (amounts.Count == 1)
                    {
                        tx.Solde = amounts[0];
                    }
                    else
                    {
                        decimal mouvement = amounts[0];
                        decimal solde = amounts[amounts.Count - 1];
                        tx.Solde = solde;
                        if (previousSolde.HasValue)
                        {
                            if (solde < previousSolde.Value) tx.Debit = mouvement;
                            else if (solde > previousSolde.Value) tx.Credit = mouvement;
                        }
                        else tx.Debit = mouvement;
                    }
                }

                previousSolde = tx.Solde;
                current.Transactions.Add(tx);
            }

            if (current != null)
            {
                current.RawSectionText = sectionRawText;
                sections.Add(current);
            }

            foreach (var sec in sections)
            {
                // BUG 4 : extrait Total Debit/Credit depuis le texte brut de CETTE section uniquement
                var totalDebitMatch = Regex.Match(sec.RawSectionText,
                    @"Total\s*(?:des\s*)?D[ée]bit(?:s)?\s*:?\s*(-?\d{1,3}(?:[ .,]\d{3})*[.,]\d{2,3})", RegexOptions.IgnoreCase);
                if (totalDebitMatch.Success) sec.TotalDebit = ParseAmount(totalDebitMatch.Groups[1].Value);

                var totalCreditMatch = Regex.Match(sec.RawSectionText,
                    @"Total\s*(?:des\s*)?Cr[ée]dit(?:s)?\s*:?\s*(-?\d{1,3}(?:[ .,]\d{3})*[.,]\d{2,3})", RegexOptions.IgnoreCase);
                if (totalCreditMatch.Success) sec.TotalCredit = ParseAmount(totalCreditMatch.Groups[1].Value);

               
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

        // Fusionne une cellule "-" isolee avec la cellule numerique qui la suit,
        // pour ne jamais perdre le signe negatif quand l'OCR separe le signe du chiffre.
        private List<string> MergeLoneSignCells(List<string> cellTexts)
        {
            var merged = new List<string>(cellTexts);
            for (int i = 0; i < merged.Count - 1; i++)
            {
                if (merged[i].Trim() == "-")
                {
                    merged[i + 1] = "-" + merged[i + 1].TrimStart();
                    merged.RemoveAt(i);
                    i--;
                }
            }
            return merged;
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