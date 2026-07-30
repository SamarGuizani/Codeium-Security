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

        private List<BankAccountSection> ExtractAccountSections(List<TableRow> rows, string fullText)
        {
            var sections = new List<BankAccountSection>();
            BankAccountSection? current = null;
            decimal? previousSolde = null;
            string lastSeenAccountNumber = "";
            string lastSeenRib = "";
            string pendingLibelleBuffer = "";
            string pendingDate = "";
            string sectionRawText = "";

            // [COMMUN - TOUTES BANQUES] Calibration des colonnes par en-tete
            int? debitAnchor = null, creditAnchor = null, soldeAnchor = null;

            // [COMMUN] Repli document-entier pour le RIB (fix #3) : certains RIB sont coupes
            // sur plusieurs lignes/cellules et ne matchent jamais correctement ligne par ligne.
            string documentRib = ExtractRib(fullText);

            // [BIAT] Detection isolee, ne touche aucune autre banque : annee de reference
            // pour les dates "jj mm" sans annee (ex: "02 01"), car DateTime.Now.Year (annee
            // systeme) est faux -> il faut l'annee reelle du releve.
            bool isBiat = fullText.Contains("BIAT", StringComparison.OrdinalIgnoreCase);
            int? biatReferenceYear = null;
            if (isBiat)
            {
                var refDateMatch = Regex.Match(fullText, @"\b\d{2}\s\d{2}\s(\d{4})\b");
                if (refDateMatch.Success)
                    biatReferenceYear = int.Parse(refDateMatch.Groups[1].Value);
            }

            foreach (var row in rows)
            {
                var cells = row.Cells.OrderBy(c => c.Left).ToList();
                var cellTextsRaw = cells
                    .Select(c => NormalizeSignSpacing(CleanWhitespace(c.Text)))
                    .ToList();
                var cellTexts = MergeLoneSignCells(cellTextsRaw);
                if (cellTexts.Count == 0) continue;

                string joined = string.Join(" ", cellTexts);
              
                // [fix #9] Nettoie les artefacts d'impression web (ex: export BTK@DIRECT) qui
                // injectent une entete/pied de page en plein milieu du contenu.
                joined = StripPrintArtifacts(joined);
                if (string.IsNullOrWhiteSpace(joined)) continue;
                sectionRawText += joined + "\n";

                // [COMMUN] Numero de compte (fix #1) : plusieurs formats selon la banque
                var account = ExtractAccountNumber(joined);
                if (string.IsNullOrWhiteSpace(account))
                    account = ExtractAccountNumber(fullText);
                if (!string.IsNullOrWhiteSpace(account))
                    lastSeenAccountNumber = account;

                // [COMMUN] RIB / Code IBAN (format TN + chiffres) (fix #3)
                var ribMatch = Regex.Match(joined, @"TN\d{2}[\s\d]{15,25}");
                if (ribMatch.Success) lastSeenRib = Regex.Replace(ribMatch.Value, @"\s+", "").Trim();

                // [COMMUN] Detection des en-tetes de colonnes Debit/Credit/Solde
                var debitCell = cells.FirstOrDefault(c => Regex.IsMatch(c.Text, @"D[ée]bit", RegexOptions.IgnoreCase));
                var creditCell = cells.FirstOrDefault(c => Regex.IsMatch(c.Text, @"Cr[ée]dit", RegexOptions.IgnoreCase));
                var soldeCell = cells.FirstOrDefault(c => Regex.IsMatch(c.Text, @"^Solde$", RegexOptions.IgnoreCase));
                if (debitCell != null || creditCell != null)
                {
                    if (debitCell != null) debitAnchor = debitCell.Left;
                    if (creditCell != null) creditAnchor = creditCell.Left;
                    if (soldeCell != null) soldeAnchor = soldeCell.Left;
                    continue;
                }

                // [ATTIJARI] "Solde (TND) au [date] : [montant]" -> capture comme SoldeFinal, PAS comme nouvelle section
                var soldeAuFinMatch = Regex.Match(joined, @"Solde\s*\(\w+\)\s*au\s*\d{2}/\d{2}/\d{4}\s*:?\s*(-?\d{1,3}(?:[ .,]\d{3})*[.,]\d{2,3})", RegexOptions.IgnoreCase);
                if (soldeAuFinMatch.Success)
                {
                    if (current != null) current.SoldeFinal = ParseAmount(soldeAuFinMatch.Groups[1].Value);
                    continue;
                }

                // [QNB + BIAT] "Solde Initial" (QNB) OU "SOLDE AU jj mm aaaa [montant]" (BIAT)
                // = debut d'une nouvelle section/sous-compte
                bool isBiatSoldeAu = Regex.IsMatch(joined, @"SOLDE\s+AU\s+\d{2}\s+\d{2}\s+\d{4}", RegexOptions.IgnoreCase);
                if (Regex.IsMatch(joined, @"Solde\s*Initial", RegexOptions.IgnoreCase) || isBiatSoldeAu)
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
                        Rib = string.IsNullOrWhiteSpace(lastSeenRib) ? documentRib : lastSeenRib,
                        Currency = ExtractCurrency(fullText),
                        SoldeInitial = soldeInit
                    };
                    previousSolde = soldeInit;
                    pendingLibelleBuffer = "";
                    pendingDate = "";
                    continue;
                }

                // [QNB] "Solde Final" = fin de section
                if (Regex.IsMatch(joined, @"Solde\s*Final", RegexOptions.IgnoreCase))
                {
                    if (current != null)
                    {
                        var finalMatch = AmountRegex.Match(joined);
                        current.SoldeFinal = finalMatch.Success ? ParseAmount(finalMatch.Value) : previousSolde;
                    }
                    continue;
                }

                // [COMMUN] Ignore les lignes de Total et de numero de page
                if (Regex.IsMatch(joined, @"\b(Total|Page\s*\d)\b", RegexOptions.IgnoreCase))
                    continue;

                if (current == null) continue;

                string normalizedDate = NormalizeDate(cellTexts[0].Trim(), biatReferenceYear);
                int dateTokensConsumed = 1;
                if (string.IsNullOrEmpty(normalizedDate) && cellTexts.Count > 1)
                {
                    string twoTokenDate = NormalizeDate(cellTexts[0].Trim() + " " + cellTexts[1].Trim(), biatReferenceYear);
                    if (!string.IsNullOrEmpty(twoTokenDate))
                    {
                        normalizedDate = twoTokenDate;
                        dateTokensConsumed = 2;
                    }
                }

                // [COMMUN] Fusionne le signe "-" isole AVEC sa position X, pour les montants de cette ligne
                var mergedWithPos = new List<(int Left, string Text)>();
                for (int i = 0; i < cells.Count; i++)
                    mergedWithPos.Add((cells[i].Left, NormalizeSignSpacing(CleanWhitespace(cells[i].Text))));
                for (int i = 0; i < mergedWithPos.Count - 1; i++)
                {
                    if (mergedWithPos[i].Text.Trim() == "-")
                    {
                        mergedWithPos[i + 1] = (mergedWithPos[i + 1].Left, "-" + mergedWithPos[i + 1].Text.TrimStart());
                        mergedWithPos.RemoveAt(i);
                        i--;
                    }
                }

                var amountCandidates = mergedWithPos
                    .Where(c => AmountRegex.IsMatch(c.Text))
                    .Select(c => new { Left = c.Left, Value = ParseAmount(AmountRegex.Match(c.Text).Value) })
                    .ToList();

                // [COMMUN] Detecte le bruit (footers, mentions legales) qui ne doit jamais rejoindre un libelle
                bool isNoise = Regex.IsMatch(joined, @"\b(Total|Page\s*\d|Solde\s*(Initial|Final)|[ée]v[èe]nements?|\(\*\)|Solde\s*\(\w+\)\s*au|BTK@?DIRECT|https?://\S+)", RegexOptions.IgnoreCase);

                if (string.IsNullOrEmpty(normalizedDate))
                {
                    if (isNoise) continue;

                    bool isPureAmountLine = amountCandidates.Count > 0;

                    if (isPureAmountLine)
                    {
                        // Vraie ligne de montants (ex: "35.700   -2.713.339"), presque aucun texte a cote
                        if (string.IsNullOrEmpty(pendingDate)) continue;

                        string fullDesc = pendingLibelleBuffer.Trim();
                        pendingLibelleBuffer = "";

                        decimal? soldeAvant2 = previousSolde;
                        var tx2 = new Transaction { Date = pendingDate, Libelle = fullDesc };
                        AssignAmounts(tx2, amountCandidates, debitAnchor, creditAnchor, soldeAnchor, ref previousSolde, isBiat);
                        ApplyMovementFallback(tx2, soldeAvant2);

                        // [fix #11] Ignore les doublons stricts (ex: repetition en haut de page
                        // lors d'une impression web comme BTK@DIRECT)
                        if (!IsDuplicateOfLast(current, tx2))
                            current.Transactions.Add(tx2);
                        pendingDate = "";
                        continue;
                    } // (ex: Y, Bottom, Line.Top...), on peut reintroduire un filtre de hauteur ici.
                    if (amountCandidates.Count == 0)
                    {
                        if (current.Transactions.Count > 0 && string.IsNullOrEmpty(pendingDate))
                        {
                            var lastTx = current.Transactions[current.Transactions.Count - 1];
                            lastTx.Libelle = (lastTx.Libelle + " " + joined.Trim()).Trim();
                        }
                        else
                        {
                            pendingLibelleBuffer = (pendingLibelleBuffer + " " + joined.Trim()).Trim();
                        }
                    }
                    continue;
                }

                // Ligne AVEC une date valide
                if (amountCandidates.Count == 0)
                {
                    // [ATTIJARI] Date + description, montant sur la ligne suivante -> on retient la date et le texte
                    pendingDate = normalizedDate;
                    string textOnly = string.Join(" ", cellTexts.Skip(dateTokensConsumed).Where(c => !AmountRegex.IsMatch(c)));
                    pendingLibelleBuffer = (pendingLibelleBuffer + " " + textOnly).Trim();
                    continue;
                }

                // [QNB] Cas normal : date + montant(s) sur la meme ligne
                string description = string.Join(" ", cellTexts.Skip(dateTokensConsumed).Where(c => !AmountRegex.IsMatch(c)))
                    .Trim(' ', '|', '[', ']', '-', '_');
                description = Regex.Replace(description, @"\b\d{2}[/\-.]\d{2}[/\-.]\d{4}\b", "").Trim();

                string fullDescription = (pendingLibelleBuffer + " " + description).Trim();
                pendingLibelleBuffer = "";
                pendingDate = "";

                decimal? soldeAvantTx = previousSolde;
                var tx = new Transaction { Date = normalizedDate, Libelle = fullDescription };
                AssignAmounts(tx, amountCandidates, debitAnchor, creditAnchor, soldeAnchor, ref previousSolde, isBiat);
                ApplyMovementFallback(tx, soldeAvantTx);

                if (!IsDuplicateOfLast(current, tx))
                    current.Transactions.Add(tx);
            }

            if (current != null)
            {
                current.RawSectionText = sectionRawText;
                sections.Add(current);
            }

            foreach (var sec in sections)
            {
                // [QNB] Format "Total Debit: X" / "Total Credit: Y" avec mot-cle repete
                var totalDebitMatch = Regex.Match(sec.RawSectionText,
                    @"Total\s*(?:des\s*)?D[ée]bit(?:s)?\s*:?\s*(-?\d{1,3}(?:[ .,]\d{3})*[.,]\d{2,3})", RegexOptions.IgnoreCase);
                if (totalDebitMatch.Success) sec.TotalDebit = ParseAmount(totalDebitMatch.Groups[1].Value);

                var totalCreditMatch = Regex.Match(sec.RawSectionText,
                    @"Total\s*(?:des\s*)?Cr[ée]dit(?:s)?\s*:?\s*(-?\d{1,3}(?:[ .,]\d{3})*[.,]\d{2,3})", RegexOptions.IgnoreCase);
                if (totalCreditMatch.Success) sec.TotalCredit = ParseAmount(totalCreditMatch.Groups[1].Value);

                // [ATTIJARI/BIAT] Repli : "Total" suivi de 2 montants sans mot-cle repete
                if (!sec.TotalDebit.HasValue && !sec.TotalCredit.HasValue)
                {
                    var totalTwoNumbers = Regex.Match(sec.RawSectionText,
                        @"Total\s+(-?\d{1,3}(?:[ .,]\d{3})*[.,]\d{2,3})\s+(-?\d{1,3}(?:[ .,]\d{3})*[.,]\d{2,3})", RegexOptions.IgnoreCase);
                    if (totalTwoNumbers.Success)
                    {
                        sec.TotalDebit = ParseAmount(totalTwoNumbers.Groups[1].Value);
                        sec.TotalCredit = ParseAmount(totalTwoNumbers.Groups[2].Value);
                    }
                }
            }

            return sections;
        }

        // [COMMUN] (fix #1) Detection du numero de compte, plusieurs formats/banques
        private string ExtractAccountNumber(string text)
        {
            var patterns = new[]
            {
                @"\b\d{2}\s\d{2}\s\d{5}\s\d{1}\b",   // [BIAT] format "75 10 00855 8" seul sur sa ligne
                @"\b\d{2,5}-\d{4,12}-\d{1,4}\b",
                @"\b\d{10,20}\b", // repli generique en DERNIER recours (le plus risque de faux positifs)
                @"Compte\s*:?\s*(\d[\d\s-]{6,25})",
                @"Account\s*Number\s*:?\s*(\d[\d\s-]{6,25})",
                @"N[°o]?\s*(?:de\s*)?Compte\s*:?\s*(\d[\d\s-]{6,25})"   // <-- accepte "N° de compte"
            };

            foreach (var pattern in patterns)
            {
                var match = Regex.Match(text, pattern, RegexOptions.IgnoreCase);
                if (match.Success)
                    return Regex.Replace(match.Value, @"\s+", "");
            }
            return "";
        }

        private string ExtractRib(string text)
        {
            var wide = Regex.Match(text, @"TN\d{2}[\d\s]{20,30}");
            if (wide.Success) return Regex.Replace(wide.Value, @"\s+", "").Trim();

            var narrow = Regex.Match(text, @"TN\d{2}[\s\d]{15,25}");
            if (narrow.Success) return Regex.Replace(narrow.Value, @"\s+", "").Trim();

            // [BIAT] RIB sans prefixe pays (format "RIB : 08 105 00075 10 00855 8 03")
            var ribLabel = Regex.Match(text, @"RIB\s*:?\s*([\d\s]{15,30})", RegexOptions.IgnoreCase);
            if (ribLabel.Success) return Regex.Replace(ribLabel.Groups[1].Value, @"\s+", "").Trim();

            return "";
        }

        // [COMMUN] Assigne Debit/Credit/Solde soit par position de colonne (fiable), soit par comparaison de solde (repli)
        private void AssignAmounts(Transaction tx, dynamic amountCandidates, int? debitAnchor, int? creditAnchor, int? soldeAnchor, ref decimal? previousSolde, bool isBiat = false)
        {
            // [fix #5] Tolerance en pixels : certaines banques decalent legerement les colonnes
            // Debit/Credit, ce qui peut inverser une classification purement basee sur la distance.
            const int Tolerance = 15;

            if (debitAnchor.HasValue && creditAnchor.HasValue)
            {
                var ambiguous = new List<dynamic>();

                foreach (var cand in amountCandidates)
                {
                    int distDebit = Math.Abs((int)cand.Left - debitAnchor.Value);
                    int distCredit = Math.Abs((int)cand.Left - creditAnchor.Value);
                    int distSolde = soldeAnchor.HasValue ? Math.Abs((int)cand.Left - soldeAnchor.Value) : int.MaxValue;

                    if (distSolde <= distDebit && distSolde <= distCredit)
                    {
                        tx.Solde = cand.Value;
                    }
                    else if (Math.Abs(distDebit - distCredit) < Tolerance)
                    {
                        // Colonne ambigue -> on tranchera apres coup via le solde plutot
                        // que sur la seule position X (peu fiable a ce niveau de tolerance).
                        ambiguous.Add(cand);
                    }
                    else if (distDebit < distCredit)
                    {
                        tx.Debit = cand.Value;
                    }
                    else
                    {
                        tx.Credit = cand.Value;
                    }
                }

                // [BIAT] Pas de colonne Solde reelle (seulement Debit/Credit) -> ne pas
                // recopier le montant du mouvement dans Solde, ca fausserait la donnee.
                bool skipSoldeFallback = isBiat && amountCandidates.Count == 1;
                if (!tx.Solde.HasValue && amountCandidates.Count > 0 && !skipSoldeFallback)
                    tx.Solde = amountCandidates[amountCandidates.Count - 1].Value;

                foreach (var cand in ambiguous)
                {
                    decimal value = cand.Value;
                    if (previousSolde.HasValue && tx.Solde.HasValue)
                    {
                        if (tx.Solde.Value < previousSolde.Value) tx.Debit = value;
                        else if (tx.Solde.Value > previousSolde.Value) tx.Credit = value;
                    }
                    else if (value < 0)
                    {
                        tx.Debit = Math.Abs(value);
                    }
                    else
                    {
                        tx.Credit = value;
                    }
                }
            }
            else
            {
                var amounts = new List<decimal>();
                foreach (var a in amountCandidates) amounts.Add((decimal)a.Value);

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
        }

        // [fix #2] Formats de date etendus (avec et sans annee)
        private string NormalizeDate(string raw, int? referenceYear = null)
        {
            raw = raw.Trim();
            string candidate = raw;

            if (raw.Length == 8 && !raw.Contains('/') && !raw.Contains('-') && !raw.Contains('.') && !raw.Contains(' '))
                candidate = $"{raw.Substring(0, 2)}/{raw.Substring(2, 2)}/{raw.Substring(4, 4)}";

            var formatsWithYear = new[]
            {
                "dd/MM/yyyy", "dd-MM-yyyy", "dd.MM.yyyy",
                "yyyy-MM-dd", "yyyy/MM/dd"
            };

            if (DateTime.TryParseExact(candidate, formatsWithYear, CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var parsed))
                return parsed.ToString("dd/MM/yyyy");

            // [BIAT] "02 01" = jour + mois separes par un espace, sans annee
            var formatsNoYear = new[] { "dd/MM", "dd-MM", "dd.MM", "dd MM" };
            string candidateNoYear = Regex.Replace(raw, @"\s+", " ").Trim(); // normalise espaces multiples
            if (DateTime.TryParseExact(candidateNoYear, formatsNoYear, CultureInfo.InvariantCulture,
                    DateTimeStyles.NoCurrentDateDefault, out var parsedNoYear))
            {
                var withYear = new DateTime(referenceYear ?? DateTime.Now.Year, parsedNoYear.Month, parsedNoYear.Day);
                return withYear.ToString("dd/MM/yyyy");
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

            // [fix #6 - NOTE] Si l'OCR "mange" un chiffre (ex: "9853.763" lu "853.763"), ce n'est PAS
            // corrige ici : le parser n'a aucun moyen de deviner un chiffre disparu sans risquer de
            // fausser des montants corrects. A traiter en amont (qualite OCR) ou via une verification
            // metier separee (coherence du nouveau solde vs solde precedent +/- mouvement).
        }

        // [fix #7] Normalise les espaces/caracteres invisibles ET le signe moins Unicode ("−" -> "-")
        private string CleanWhitespace(string text) =>
            text.Replace('\u00A0', ' ')
                .Replace('\u202F', ' ')
                .Replace('\u2212', '-');

        // [fix #7] Recolle un signe "-" separe de son montant par un espace dans la meme cellule
        // (ex: "-   150.000" -> "-150.000"), sans toucher aux cas deja geres par MergeLoneSignCells.
        private string NormalizeSignSpacing(string text) =>
            Regex.Replace(text, @"^(\s*-)\s+(?=\d)", "-");

        // [fix #9] Les impressions "web" de certains portails (ex: export BTK@DIRECT) injectent une
        // entete/pied de page en plein milieu du contenu : URL, pagination "x/y", horodatage
        // navigateur, filigrane du portail, debut de l'entete suivante ("Date de valeur...").
        // Des qu'on detecte le debut de ce bloc (l'URL), on tronque : le reste n'appartient pas
        // au libelle de la transaction.
        private string StripPrintArtifacts(string text)
        {
            var urlMatch = Regex.Match(text, @"https?://\S+", RegexOptions.IgnoreCase);
            if (urlMatch.Success)
                text = text.Substring(0, urlMatch.Index);

            // Filigrane / horodatage residuels si jamais coupes autrement (sans URL devant,
            // ou repartis sur plusieurs cellules distinctes)
            text = Regex.Replace(text, @"\bBTK@?DIRECT\b", "", RegexOptions.IgnoreCase);
            text = Regex.Replace(text, @"\b\d{1,2}/\d{1,2}/\d{2,4},?\s*\d{1,2}:\d{2}\s*(AM|PM)\b", "", RegexOptions.IgnoreCase);

            return CleanWhitespace(text).Trim();
        }

        // [fix #10] Repli : si aucune colonne Debit/Credit n'a pu etre assignee (montant illisible,
        // coupe par du bruit OCR/impression, colonnes non calibrees...) mais que le solde avant et
        // apres la ligne sont tous les deux connus, on deduit le mouvement par simple difference.
        private void ApplyMovementFallback(Transaction tx, decimal? soldeAvant)
        {
            if (tx.Debit.HasValue || tx.Credit.HasValue) return;
            if (!soldeAvant.HasValue || !tx.Solde.HasValue) return;

            decimal diff = tx.Solde.Value - soldeAvant.Value;
            if (diff < 0) tx.Debit = Math.Abs(diff);
            else if (diff > 0) tx.Credit = diff;
        }

        private bool IsDuplicateOfLast(BankAccountSection section, Transaction tx)
        {
            if (section.Transactions.Count == 0) return false;
            var last = section.Transactions[section.Transactions.Count - 1];
            return last.Date == tx.Date
                && last.Libelle == tx.Libelle
                && last.Debit == tx.Debit
                && last.Credit == tx.Credit
                && last.Solde == tx.Solde;
        }

        private string ExtractCurrency(string text)
        {
            if (text.Contains("TND")) return "TND";
            if (text.Contains("DINAR")) return "TND";
            if (text.Contains("EUR")) return "EUR";
            if (text.Contains("USD")) return "USD";
            return "";
        }

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