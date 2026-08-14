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

        //private static readonly Regex AmountRegex =
        //new(@"-?\d{1,3}(?:[ .,]\d{3})*[.,]\d{2,3}");
        //correction des nombres qui sont avec espaces done 
        private static readonly Regex AmountRegex =
new(@"-?\d{1,3}(?:[ .,]?\d{3})*[.,]\d{2,3}");

       
        private static readonly Regex ArabicScriptRegex = new("[؀-ۿ]");

        private List<TableRow> SplitDuplicatedRows(List<TableRow> rows)
        {
            var result = new List<TableRow>();
            foreach (var row in rows)
            {
                string joined = string.Join(" ", row.Cells.Select(c => c.Text));
           
                if (HasRepeatedSubstring(joined, minLength: 15))
                {
                    Console.WriteLine($"[WARN] Row potentiellement fusionnée (texte dupliqué) : {joined}");
                }
                result.Add(row);
            }
            return result;
        }

        private bool HasRepeatedSubstring(string text, int minLength)
        {
            for (int i = 0; i + minLength * 2 <= text.Length; i++)
            {
                string chunk = text.Substring(i, minLength);
                if (text.IndexOf(chunk, i + minLength, StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            }
            return false;
        }
        public object Parse(string fullText, List<TextLine> lines)
        {
            var engine = new DocumentAnalysisEngine();

            string bankName = ExtractBankName(fullText);
            bool isBiat = bankName.Contains("BIAT", StringComparison.OrdinalIgnoreCase);
            bool isZitouna = fullText.Contains("ZITOUNA", StringComparison.OrdinalIgnoreCase);
            bool isBtk = fullText.Contains("BTK", StringComparison.OrdinalIgnoreCase);
            bool isBna = fullText.Contains("BNA", StringComparison.OrdinalIgnoreCase);
            bool isWifak = fullText.Contains("WIFAK", StringComparison.OrdinalIgnoreCase);
            // [ATB] La colonne "Jour" ne contient que le quantième (ex. "03"), jamais une date
            // complète. La vraie date ("DATE VALEUR", format dd/mm/yy) est une cellule à part,
            // plus loin dans la ligne (après libellé + référence d'opération).
            bool isAtb = fullText.Contains("ATB", StringComparison.OrdinalIgnoreCase)
     || fullText.Contains("Arab Tunisian Bank", StringComparison.OrdinalIgnoreCase);
            bool isUbci = fullText.Contains("UBCI", StringComparison.OrdinalIgnoreCase);
            bool bhSignalCompte = Regex.IsMatch(fullText, @"No\s*du\s*compte\s*[-:]", RegexOptions.IgnoreCase);
            bool bhSignalTitulaire = Regex.IsMatch(fullText, @"du\s*compte\s*:\s*\S", RegexOptions.IgnoreCase);
            bool bhSignalPeriode = Regex.IsMatch(fullText, @"Op[ée]rations\s+du\s+\d{2}/\d{2}/\d{4}\s+au\s+\d{2}/\d{2}/\d{4}", RegexOptions.IgnoreCase);
            int bhSignalCount = (bhSignalCompte ? 1 : 0) + (bhSignalTitulaire ? 1 : 0) + (bhSignalPeriode ? 1 : 0);

            bool isBh = fullText.Contains("bhbank", StringComparison.OrdinalIgnoreCase)
                || fullText.Contains("BH BANK", StringComparison.OrdinalIgnoreCase)
                || fullText.Contains("Banque de l'Habitat", StringComparison.OrdinalIgnoreCase)
                || bankName.Contains("Habitat", StringComparison.OrdinalIgnoreCase)
                || bankName.Contains("(BH)", StringComparison.OrdinalIgnoreCase)
                || bhSignalCount >= 2;

            if (isBh)
            {
                var bhDocument = new BankDocument
                {
                    BankName = "Banque de l'Habitat (BH)",
                    Accounts = ExtractBhAccountSections(fullText)
                };
                return bhDocument;
            }
            // [UBCI] Detection + contexte pour les 3 regles UBCI (solde, dates deformees,
            if (isBiat || isZitouna || isBtk || isBna || isBh || isWifak || isUbci)
                engine.VerticalTolerance = 1;

            var rows = engine.BuildTable(lines);
            rows = SplitDuplicatedRows(rows);
            if (isUbci)
            {
                var ubciDocument = new BankDocument
                {
                    BankName = "Union Bancaire pour le Commerce et l'Industrie (UBCI)",
                    Accounts = ExtractUbciAccountSections(rows, fullText)
                };
                return ubciDocument;
            }
            var document = new BankDocument
            {
                BankName = bankName,
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

            bool biatInNoiseZone = false;
            int? debitAnchor = null, creditAnchor = null, soldeAnchor = null, montantAnchor = null, dateAnchor = null; string documentRib = ExtractRib(fullText);

           
            int? documentYear = null;
            bool isBiat = fullText.Contains("BIAT", StringComparison.OrdinalIgnoreCase);

            var dateMatch = Regex.Match(fullText, @"\b(\d{1,2})\s+(\d{1,2})\s+(\d{4})\b");
            if (dateMatch.Success && int.TryParse(dateMatch.Groups[3].Value, out int year))
                documentYear = year;
            else
            {
                var soldeMatch = Regex.Match(fullText, @"SOLDE\s+AU\s+\d{1,2}\s+\d{1,2}\s+(\d{4})", RegexOptions.IgnoreCase);
                if (soldeMatch.Success && int.TryParse(soldeMatch.Groups[1].Value, out year))
                    documentYear = year;
                else
                {
                    // Repli generique : n'importe quelle date "jj moisAbrege aa/aaaa" trouvee n'importe
                    // ou dans le document (en-tete "Edité le", "Imprimé le", "Solde départ au", ou
                    // simplement la premiere date rencontree dans le texte) donne l'annee du releve.
                    var frDateMatch = Regex.Match(fullText,
                        @"\d{1,2}\s+[A-Za-zÀ-ÿ]{3,6}\.?\s*(\d{2,4})",
                        RegexOptions.IgnoreCase);
                    if (frDateMatch.Success && int.TryParse(frDateMatch.Groups[1].Value, out year))
                        documentYear = year < 100 ? 2000 + year : year;
                }
            }


            // [QNB] Détection de la banque
            bool isQnb = fullText.Contains("QNB", StringComparison.OrdinalIgnoreCase);
            List<string> qnbAccountNumbers = new List<string>();
            int qnbAccountIndex = 0;
            if (isQnb)
            {
                qnbAccountNumbers = Regex.Matches(fullText, @"\b\d{4}-\d{6}-\d{3}\b")
                    .Select(m => m.Value)
                    .Distinct()
                    .ToList();
            }
            bool isAmenDocument = fullText.IndexOf("AMEN", StringComparison.OrdinalIgnoreCase) >= 0;
            bool isBtk = fullText.Contains("BTK", StringComparison.OrdinalIgnoreCase);

            // [BNA] Chaque operation "principale" (Date+Libelle+Valeur+Montant+Solde) est suivie
           
            bool isBna = fullText.Contains("BNA", StringComparison.OrdinalIgnoreCase);

            // [ATB] Meme detection que dans Parse() : la date n'est jamais dans les premieres
            // cellules, voir usage plus bas (GetDateFromAnyCell).
            bool isAtb = fullText.Contains("ATB", StringComparison.OrdinalIgnoreCase)
                || fullText.Contains("Arab Tunisian Bank", StringComparison.OrdinalIgnoreCase);

            // [BH] Meme detection que dans Parse() : format lineaire, une seule colonne montant.
            bool isBh = fullText.Contains("bhbank", StringComparison.OrdinalIgnoreCase)
                 || fullText.Contains("BH BANK", StringComparison.OrdinalIgnoreCase)
                 || fullText.Contains("Banque de l'Habitat", StringComparison.OrdinalIgnoreCase);
            
            bool isUbci = fullText.Contains("UBCI", StringComparison.OrdinalIgnoreCase);
            DateTime? ubciPeriodStart = null, ubciPeriodEnd = null;
            if (isUbci)
            {
                var ubciPeriodeMatch = Regex.Match(fullText, @"PERIODE\s+DU\s+(\d{2}/\d{2}/\d{4})\s+AU\s+(\d{2}/\d{2}/\d{4})", RegexOptions.IgnoreCase);
                if (ubciPeriodeMatch.Success)
                {
                    if (DateTime.TryParseExact(ubciPeriodeMatch.Groups[1].Value, "dd/MM/yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var ubciPs))
                        ubciPeriodStart = ubciPs;
                    if (DateTime.TryParseExact(ubciPeriodeMatch.Groups[2].Value, "dd/MM/yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var ubciPe))
                        ubciPeriodEnd = ubciPe;
                }
                
            }
            DateTime? ubciLastConfirmedDate = null;
            bool ubciClotureReached = false;

          
            bool TryRepairUbciDate(string rawDigits, out string repaired)
            {
                repaired = "";
                if (rawDigits.Length != 9 && rawDigits.Length != 10) return false;
                if (!Regex.IsMatch(rawDigits, @"^\d+$")) return false;

                var candidates = new List<string>();
                if (rawDigits.Length == 9)
                {
                    candidates.Add(rawDigits.Remove(2, 1));
                    candidates.Add(rawDigits.Remove(4, 1));
                }
                else
                {
                    candidates.Add(rawDigits.Remove(5, 1).Remove(2, 1));
                }

                foreach (var candidate in candidates)
                {
                    if (candidate.Length != 8) continue;
                    if (!DateTime.TryParseExact(candidate, "ddMMyyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
                        continue;
                    if (ubciPeriodStart.HasValue && parsed < ubciPeriodStart.Value.AddDays(-5)) continue;
                    if (ubciPeriodEnd.HasValue && parsed > ubciPeriodEnd.Value.AddDays(5)) continue;
                    if (ubciLastConfirmedDate.HasValue && Math.Abs((parsed - ubciLastConfirmedDate.Value).TotalDays) > 15) continue;

                    repaired = parsed.ToString("dd/MM/yyyy");
                    return true;
                }
                return false;
            }
            // Document utilise des montants signes (QNB, Zitouna, BIAT-extrait...) :
            // negatif = Debit, positif = Credit, strictement, partout dans ce document.
            bool hasSignedAmounts = Regex.IsMatch(fullText, @"-\d{1,3}(?:[ .,]?\d{3})*[.,]\d{2,3}");

            // [GENERIQUE] Detection de bruit d'en-tete/pied de page par repetition, independante
            
            var repeatedLineCounts = new Dictionary<string, int>();
            foreach (var noiseRow in rows)
            {
                string noiseJoined = StripPrintArtifacts(string.Join(" ",
                    noiseRow.Cells.Select(c => NormalizeSignSpacing(CleanWhitespace(c.Text)))));
               
                if (noiseJoined.Length < 40) continue;
                string noiseKey = Regex.Replace(noiseJoined, @"\d", "#").Trim();
                repeatedLineCounts[noiseKey] = repeatedLineCounts.GetValueOrDefault(noiseKey) + 1;
            }
            if (isBh)
            {
                var splitRows = new List<TableRow>();
                foreach (var row in rows)
                {
                    string rawJoined = string.Join(" ", row.Cells.Select(c => c.Text));
                    if (Regex.IsMatch(rawJoined, @"(?<=\d)\d{2}/\d{2}/\d{4}"))
                    {
                        string repaired = Regex.Replace(rawJoined, @"(?<=\d)(\d{2}/\d{2}/\d{4})", "\n$1");
                        foreach (var fragment in repaired.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                        {
                            var fakeRow = new TableRow();
                            fakeRow.Cells.Add(new TableCell { Text = fragment.Trim(), Left = row.Cells.FirstOrDefault()?.Left ?? 0 });
                            splitRows.Add(fakeRow);
                        }
                    }
                    else
                    {
                        splitRows.Add(row);
                    }
                }
                rows = splitRows;
            }

            bool skippingSummaryTable = Regex.IsMatch(fullText, @"Resum[ée]'?\s+du\s+compte", RegexOptions.IgnoreCase);
            foreach (var row in rows)
            {
                var cells = row.Cells.OrderBy(c => c.Left).ToList();
                var cellTextsRaw = cells
                    .Select(c => NormalizeSignSpacing(CleanWhitespace(c.Text)))
                    .ToList();
                var cellTexts = MergeLoneSignCells(cellTextsRaw);
                if (cellTexts.Count == 0) continue;


                string joined = string.Join(" ", cellTexts);
                joined = StripPrintArtifacts(joined);
                if (string.IsNullOrWhiteSpace(joined)) continue;

                if (skippingSummaryTable)
                {
                    if (Regex.IsMatch(joined, @"Account\s*\(IBAN\)", RegexOptions.IgnoreCase))
                        skippingSummaryTable = false;
                    continue;
                }

                sectionRawText += joined + "\n";
                //update le 01/08/2026
                // ===== TRAITEMENT SPÉCIFIQUE QNB =====
                if (isQnb)
                {
                    Console.WriteLine($"[QNB-DEBUG] joined='{joined}' | current==null: {current == null}");
                    var qnbMatch = Regex.Match(joined,
                        @"^(\d{2}/\d{2}/\d{4})\s+(.*?)\s+(-?\d{1,3}(?:[ .,]?\d{3})*[.,]\d{2,3})\s+(-?\d{1,3}(?:[ .,]?\d{3})*[.,]\d{2,3})$", RegexOptions.IgnoreCase);
                    if (qnbMatch.Success)
                    {
                        string date = qnbMatch.Groups[1].Value;
                        string libelle = qnbMatch.Groups[2].Value.Trim();
                        decimal debit = ParseAmount(qnbMatch.Groups[3].Value);
                        decimal solde = ParseAmount(qnbMatch.Groups[4].Value);

                        if (current == null)
                        {
                            current = new BankAccountSection
                            {
                                AccountNumber = lastSeenAccountNumber,
                                Rib = string.IsNullOrWhiteSpace(lastSeenRib) ? documentRib : lastSeenRib,
                                Currency = ExtractCurrency(fullText),
                                SoldeInitial = null
                            };
                            sectionRawText = joined + "\n";
                        }

                        var qnbTx = new Transaction
                        {
                            Date = NormalizeDate(date, null),
                            Libelle = libelle,
                            //Solde = solde
                        };

                        decimal absVal = Math.Abs(debit);
                        if (debit < 0) qnbTx.Debit = absVal;
                        else if (debit > 0) qnbTx.Credit = absVal;


                        previousSolde = solde;
                        current.Transactions.Add(qnbTx);
                        continue;
                    }
                }

                // ===== FIN TRAITEMENT QNB =====
                // ===== TRAITEMENT SPÉCIFIQUE BH (format linéaire, une seule colonne montant) =====
                if (isBh)
                {
                    
                    var bhMatch = Regex.Match(joined,
                        @"^(\d{2}/\d{2}/\d{4})\s+(.+?)\s+(\d{2}/\d{2}/\d{4})\s*(-?\d[\d\s.,]*\d|\d)$");

                    if (bhMatch.Success)
                    {
                        string dateOp = bhMatch.Groups[1].Value;
                        string libelle = bhMatch.Groups[2].Value.Trim();
                        decimal montant = ParseAmount(bhMatch.Groups[4].Value);

                        if (current == null)
                        {
                            current = new BankAccountSection
                            {
                                AccountNumber = lastSeenAccountNumber,
                                Rib = string.IsNullOrWhiteSpace(lastSeenRib) ? documentRib : lastSeenRib,
                                Currency = ExtractCurrency(fullText),
                                SoldeInitial = null
                            };
                            sectionRawText = joined + "\n";
                        }

                        var bhTx = new Transaction
                        {
                            Date = NormalizeDate(dateOp, null),
                            Libelle = libelle
                        };

                  
                        if (IsBhDebitLibelle(libelle))
                            bhTx.Debit = montant;
                        else
                            bhTx.Credit = montant;

                        current.Transactions.Add(bhTx);
                        continue;
                    }

                    
                }
                // ===== FIN TRAITEMENT BH =====
                // Extraction du numéro de compte
                var account = ExtractAccountNumber(joined);
                //if (string.IsNullOrWhiteSpace(account))
                // account = ExtractAccountNumber(fullText);//je doit supprimer cette lignes le 3 aout 
                if (!string.IsNullOrWhiteSpace(account))
                    lastSeenAccountNumber = account;

                // RIB
                var ribMatch = Regex.Match(joined, @"TN\d{2}[\s\d]{15,25}");
                if (ribMatch.Success) lastSeenRib = Regex.Replace(ribMatch.Value, @"\s+", "").Trim();

                // [UBCI] Solde debiteur/crediteur d'ouverture : "SOLDE DEBITEUR AU <date>
             
                if (isUbci)
                {
                    var ubciOpenMatch = Regex.Match(joined,
                        @"SOLDE\s+(DEBITEUR|CREDITEUR)\s+AU\s+\d{2}/\d{2}/\d{4}\s+(-?\d{1,3}(?:[ .,]\d{3})*[.,]\d{2,3})",
                        RegexOptions.IgnoreCase);
                    if (current == null && ubciOpenMatch.Success)
                    {
                        decimal ubciSoldeInit = ParseAmount(ubciOpenMatch.Groups[2].Value);
                        // Le releve n'imprime pas toujours le signe "-" pour un solde debiteur
                        // (observe sur bq bidah 02-2025ubci.pdf) : le mot DEBITEUR fait foi.
                        ubciSoldeInit = ubciOpenMatch.Groups[1].Value.Equals("DEBITEUR", StringComparison.OrdinalIgnoreCase)
                            ? -Math.Abs(ubciSoldeInit)
                            : Math.Abs(ubciSoldeInit);

                        current = new BankAccountSection
                        {
                            AccountNumber = lastSeenAccountNumber,
                            Rib = string.IsNullOrWhiteSpace(lastSeenRib) ? documentRib : lastSeenRib,
                            Currency = ExtractCurrency(fullText),
                            SoldeInitial = ubciSoldeInit
                        };
                        previousSolde = ubciSoldeInit;
                    if (ribMatch.Success) lastSeenRib = Regex.Replace(ribMatch.Value, @"\s+", "").Trim();      sectionRawText = joined + "\n";
                        pendingLibelleBuffer = "";
                        pendingDate = "";
                        continue;
                    }

                    // [UBCI] Solde de cloture : "SOLDE DE CLOTURE <montant>". Marque aussi la fin
                    // des reparations UBCI (dates/montants) pour cette section : tout ce qui suit
                    // est du pied de page (mentions legales, coordonnees), jamais une operation.
                    var ubciCloseMatch = Regex.Match(joined, @"SOLDE\s+DE\s+CLOTURE\s+(-?\d{1,3}(?:[ .,]\d{3})*[.,]\d{2,3})", RegexOptions.IgnoreCase);
                    if (ubciCloseMatch.Success)
                    {
                        if (current != null) current.SoldeFinal = ParseAmount(ubciCloseMatch.Groups[1].Value);
                        ubciClotureReached = true;
                        continue;
                    }
                }

                // Détection des en-têtes Debit/Credit/Solde
                var debitCell = cells.FirstOrDefault(c => Regex.IsMatch(c.Text, @"D[ée]bit", RegexOptions.IgnoreCase));
                var creditCell = cells.FirstOrDefault(c => Regex.IsMatch(c.Text, @"Cr[ée]dit", RegexOptions.IgnoreCase));
                var soldeCell = cells.FirstOrDefault(c => Regex.IsMatch(c.Text, @"\bSolde\b", RegexOptions.IgnoreCase));
                var montantCell = cells.FirstOrDefault(c => Regex.IsMatch(c.Text, @"\bMontant\b", RegexOptions.IgnoreCase));
                var dateCell = cells.FirstOrDefault(c => Regex.IsMatch(c.Text, @"^Date$|Date\s*op[ée]ration", RegexOptions.IgnoreCase));
                if (isBiat)
                {
                    
                    var arabicDebit = cells.FirstOrDefault(c => IsBiatArabicHeaderCell(c.Text, "عليه"));
                    var arabicCredit = cells.FirstOrDefault(c => IsBiatArabicHeaderCell(c.Text, "له"));
                    if (arabicDebit != null) debitCell = arabicDebit;
                    if (arabicCredit != null) creditCell = arabicCredit;
                }

               
                bool biatLooksLikeTransactionRow = isBiat && (debitCell != null || creditCell != null) &&
                    (!string.IsNullOrEmpty(GetNormalizedDateFromCells(cellTexts, documentYear)) || cells.Any(c => AmountRegex.IsMatch(c.Text)));

                if ((debitCell != null || creditCell != null) && !biatLooksLikeTransactionRow)
                {
                    if (debitCell != null) debitAnchor = debitCell.Left;
                    if (creditCell != null) creditAnchor = creditCell.Left;
                    if (soldeCell != null) soldeAnchor = soldeCell.Left;
                    if (dateCell != null) dateAnchor = dateCell.Left;
                    if (montantCell != null) montantAnchor = montantCell.Left;

                    if (debitAnchor.HasValue && creditAnchor.HasValue && Math.Abs(debitAnchor.Value - creditAnchor.Value) < 40)
                    {
                        var (inferredDebit, inferredCredit) = InferAnchorsFromAmountPositions(rows);
                        if (inferredDebit.HasValue && inferredCredit.HasValue)
                        {
                            debitAnchor = inferredDebit;
                            creditAnchor = inferredCredit;
                        }
                    }
                    continue;
                }

                // [ATTIJARI] Solde (TND) au ...
                var soldeAuFinMatch = Regex.Match(joined, @"Solde\s*\(\w+\)\s*au\s*\d{2}/\d{2}/\d{4}\s*:?\s*(-?\d{1,3}(?:[ .,]\d{3})*[.,]\d{2,3})", RegexOptions.IgnoreCase);
                if (soldeAuFinMatch.Success)
                {
                    if (current != null) current.SoldeFinal = ParseAmount(soldeAuFinMatch.Groups[1].Value);
                    continue;
                }

                // [QNB - AMÉLIORÉ] "Solde Initial" avec montant
                var soldeInitMatch = Regex.Match(joined, @"\bSolde\s+Initial\s*([-+]?\d{1,3}(?:[ .,]\d{3})*[.,]\d{2,3})", RegexOptions.IgnoreCase);
                if (soldeInitMatch.Success || Regex.IsMatch(joined, @"\bSolde\s+Initial\b", RegexOptions.IgnoreCase))
                {
                    // Fermer la section précédente
                    if (current != null)
                    {
                        current.RawSectionText = sectionRawText;
                        sections.Add(current);
                    }
                    sectionRawText = joined + "\n";

                    // Récupérer le montant s'il est présent, sinon 0
                    decimal soldeInit = 0;
                    if (soldeInitMatch.Success)
                        soldeInit = ParseAmount(soldeInitMatch.Groups[1].Value);

                    string resolvedAccountNumber = lastSeenAccountNumber;
                    if (isQnb && qnbAccountIndex < qnbAccountNumbers.Count)
                    {
                        resolvedAccountNumber = qnbAccountNumbers[qnbAccountIndex];
                        qnbAccountIndex++;
                    }

                    current = new BankAccountSection
                    {
                        AccountNumber = resolvedAccountNumber,
                        Rib = string.IsNullOrWhiteSpace(lastSeenRib) ? documentRib : lastSeenRib,
                        Currency = ExtractCurrency(fullText),
                        SoldeInitial = soldeInit
                    };
                    previousSolde = soldeInit;
                    pendingLibelleBuffer = "";
                    pendingDate = "";
                    continue;
                }

                // [BIAT] SOLDE AU ...
                var biatSoldeAuMatch = Regex.Match(joined, @"SOLDE\s*AU\s*\d{1,2}\s+\d{1,2}\s+\d{4}\s+(-?\d{1,3}(?:[ .,]\d{3})*[.,]\d{2,3})", RegexOptions.IgnoreCase);
                if (biatSoldeAuMatch.Success)
                {
                    if (current != null)
                    {
                        current.RawSectionText = sectionRawText;
                        sections.Add(current);
                    }
                    sectionRawText = joined + "\n";

                    decimal biatSoldeInit = ParseAmount(biatSoldeAuMatch.Groups[1].Value);
                    current = new BankAccountSection
                    {
                        AccountNumber = lastSeenAccountNumber,
                        Rib = string.IsNullOrWhiteSpace(lastSeenRib) ? documentRib : lastSeenRib,
                        Currency = ExtractCurrency(fullText),
                        SoldeInitial = biatSoldeInit
                    };
                    previousSolde = biatSoldeInit;
                    pendingLibelleBuffer = "";
                    pendingDate = "";
                    continue;
                }

                // [ZITOUNA] Solde actuel
                var zitounaSoldeMatch = Regex.Match(joined, @"Solde\s+actuel\s+(-?\d{1,3}(?:[ .,]\d{3})*[.,]\d{2,3})", RegexOptions.IgnoreCase);
                if (zitounaSoldeMatch.Success)
                {
                    if (current != null)
                    {
                        current.RawSectionText = sectionRawText;
                        sections.Add(current);
                    }
                    sectionRawText = joined + "\n";

                    decimal soldeInit = ParseAmount(zitounaSoldeMatch.Groups[1].Value);
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

                // [QNB] "Solde Final"
                if (Regex.IsMatch(joined, @"\bSolde\s+Final\b", RegexOptions.IgnoreCase))
                {
                    if (current != null)
                    {
                        var finalMatch = AmountRegex.Match(joined);
                        current.SoldeFinal = finalMatch.Success ? ParseAmount(finalMatch.Value) : previousSolde;
                    }
                    continue;
                }

                // [BIAT] Devise SOLDE ...
                var biatSoldeFinalMatch = Regex.Match(joined, @"\bSOLDE\b(?!\s*AU)\s+(-?\d{1,3}(?:[ .,]\d{3})*[.,]\d{2,3})", RegexOptions.IgnoreCase);
                if (biatSoldeFinalMatch.Success)
                {
                    if (current != null) current.SoldeFinal = ParseAmount(biatSoldeFinalMatch.Groups[1].Value);
                    continue;
                }

             
                if (current == null
                    && Regex.IsMatch(joined, @"\bSOLDE\b", RegexOptions.IgnoreCase)
                    && Regex.IsMatch(joined, @"\d{1,2}[/\-. ]\d{1,2}[/\-. ]\d{2,4}|\d{1,2}\s+[A-Za-z]{3,4}\s+\d{2,4}"))
                {
                    var genericSoldeAmounts = AmountRegex.Matches(joined);
                    if (genericSoldeAmounts.Count == 1)
                    {
                        decimal genericSoldeInit = ParseAmount(genericSoldeAmounts[0].Value);
                        current = new BankAccountSection
                        {
                            AccountNumber = lastSeenAccountNumber,
                            Rib = string.IsNullOrWhiteSpace(lastSeenRib) ? documentRib : lastSeenRib,
                            Currency = ExtractCurrency(fullText),
                            SoldeInitial = genericSoldeInit
                        };
                        previousSolde = genericSoldeInit;
                        sectionRawText = joined + "\n";
                        pendingLibelleBuffer = "";
                        pendingDate = "";
                        continue;
                    }
                }

                // Ignorer les lignes de Total et de page
                if (Regex.IsMatch(joined, @"\b(Total|Page\s*\d)\b", RegexOptions.IgnoreCase))
                    continue;

                // [AMEN] Ligne d'echo de date au format POINT
                if (isAmenDocument && current != null && current.Transactions.Count > 0
                    && !joined.Contains('/') && Regex.IsMatch(joined.Trim(), @"^\d{2}\.\d{2}\.\d{4}\s*\S*$"))
                {
                    var lastTx = current.Transactions[current.Transactions.Count - 1];
                    lastTx.Libelle = (lastTx.Libelle + " " + joined.Trim()).Trim();
                    continue;
                }

                // [QNB] Filtrer les footers et bruits (renforcé pour QNB)
                if (isQnb && Regex.IsMatch(joined, @"\b(Pour toute remarque|N\.B:|support@|hotline|Page\s*\d+ of \d+)\b", RegexOptions.IgnoreCase))
                    continue;

                // [BTK] Filtrer le bruit d'en-tete/pied de page repete a chaque saut de page
                // (ex: "Titulaire du compte : ...", "Valeur Libellé de l'opération Solde (TND)",
                // "Report du 11/01/2024 127,9", "4 Page sur 112"). Sans ce filtre, ces lignes
               
                if (isBtk && Regex.IsMatch(joined, @"Titulaire\s+du\s+compte|Valeur\s+Libell[ée]|Report\s+du\s+\d{2}[/\-.]\d{2}[/\-.]\d{2,4}|Page\s+sur\s+\d+", RegexOptions.IgnoreCase))
                    continue;

                // Normalisation de la date
                string normalizedDate = GetNormalizedDateFromCells(cellTexts, documentYear);

                // [BNA] La date d'une sous-ligne (Com..., TVA) est souvent dans une cellule qui
               
                if (isBna && string.IsNullOrEmpty(normalizedDate))
                {
                    normalizedDate = GetDateFromAnyCell(cellTexts, null);
                }
                // [ATB] Même raisonnement que BNA ci-dessus : la date n'est jamais dans les 3
                
                if (isAtb && string.IsNullOrEmpty(normalizedDate))
                {
                    normalizedDate = GetDateFromAnyCell(cellTexts, null);
                }
                // [UBCI] Filet de secours : uniquement si le chemin normal (inchangé) n'a rien
                
                if (isUbci && string.IsNullOrEmpty(normalizedDate) && !ubciClotureReached
                    && cellTexts.Count > 0 && Regex.IsMatch(cellTexts[0], @"^\d{9,10}$"))
                {
                    if (TryRepairUbciDate(cellTexts[0], out var ubciRepairedDate))
                        normalizedDate = ubciRepairedDate;
                }

                if (isUbci && !string.IsNullOrEmpty(normalizedDate)
                    && DateTime.TryParseExact(normalizedDate, "dd/MM/yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var ubciConfirmedDate))
                {
                    ubciLastConfirmedDate = ubciConfirmedDate;
                }

                // Fusion des signes "-" isolés
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

                // AJOUT : élimine les doublons exacts (même valeur, positions très proches)
                
                // causés par une fusion accidentelle de 2 lignes PDF en une seule row.
               amountCandidates = amountCandidates
                    .GroupBy(c => new { c.Value, ZoneLeft = c.Left / 20 }) // regroupe par valeur + zone de 20px
                    .Select(g => g.First())
                    .ToList();
                //update le 3 aout 
                
                if (debitAnchor.HasValue || creditAnchor.HasValue || soldeAnchor.HasValue)
                {
                    // [UBCI] Meme mecanisme que ci-dessus (montant sans separateur), etendu aux
                    
                    bool shortAmountsAllowed = ((isUbci && !ubciClotureReached) || isBna) && Regex.IsMatch(joined, @"[A-Za-zÀ-ÿ]");
                    string bareDigitPattern = shortAmountsAllowed ? @"^-?\d{3,10}$" : @"^-?\d{5,10}$";
                    var bareDigitCandidates = mergedWithPos
                        .Where(c => !AmountRegex.IsMatch(c.Text))
                        .Where(c => Regex.IsMatch(c.Text.Trim(), bareDigitPattern))
                        .Where(c =>
                        {
                            int distDebit = debitAnchor.HasValue ? Math.Abs(c.Left - debitAnchor.Value) : int.MaxValue;
                            int distCredit = creditAnchor.HasValue ? Math.Abs(c.Left - creditAnchor.Value) : int.MaxValue;
                            int distSolde = soldeAnchor.HasValue ? Math.Abs(c.Left - soldeAnchor.Value) : int.MaxValue;
                            int minDist = Math.Min(distDebit, Math.Min(distCredit, distSolde));
                            return minDist < 60;
                        })
                        .Select(c =>
                        {
                            string digits = c.Text.Trim();
                            bool neg = digits.StartsWith("-");
                            if (neg) digits = digits.Substring(1);
                            // Partie entiere vide (ex. "555" -> decimales seules) : equivaut a 0.
                         
                            // (toujours >=2 chiffres de partie entiere) - purement defensif ici.
                            string intPart = digits.Length > 3 ? digits.Substring(0, digits.Length - 3) : "0";
                            string decPart = digits.Substring(digits.Length - 3);
                            decimal val = decimal.Parse(intPart + "." + decPart, CultureInfo.InvariantCulture);
                            if (neg) val = -val;
                            return new { Left = c.Left, Value = val };
                        })
                        .ToList();

                    amountCandidates.AddRange(bareDigitCandidates);
                    amountCandidates = amountCandidates.OrderBy(a => a.Left).ToList();
                }

                // Création d'une section par défaut si aucune détection précédente
                if (current == null)
                {
                    if (!string.IsNullOrEmpty(normalizedDate) && amountCandidates.Count > 0)
                    {
                        current = new BankAccountSection
                        {
                            AccountNumber = lastSeenAccountNumber,
                            Rib = string.IsNullOrWhiteSpace(lastSeenRib) ? documentRib : lastSeenRib,
                            Currency = ExtractCurrency(fullText),
                            SoldeInitial = null
                        };
                        sectionRawText = joined + "\n";
                    }
                    else
                    {
                        continue;
                    }
                }

                // Détection du bruit général
                bool biatNoiseHit = isBiat && IsBiatNoise(joined);
                if (biatNoiseHit) biatInNoiseZone = true;

               
                string repeatedNoiseKey = Regex.Replace(joined, @"\d", "#").Trim();
                bool isRepeatedBoilerplate = amountCandidates.Count == 0
                    && joined.Length >= 40
                    && repeatedLineCounts.GetValueOrDefault(repeatedNoiseKey) >= 3;

                // [BNA] "lSolde ... a ce jour sauf erreur ou omission ..." est le rappel de solde
               
                bool bnaNoiseHit = isBna && Regex.IsMatch(joined, @"ce\s*jour\s*sauf\s*erreur\s*ou\s*omission", RegexOptions.IgnoreCase);

                bool isNoise = Regex.IsMatch(joined, @"\b(Total|Page\s*\d|Solde\s*(Initial|Final)|[ée]v[èe]nements?|\(\*\)|Solde\s*\(\w+\)\s*au|BTK@?DIRECT|https?://\S+)", RegexOptions.IgnoreCase)
                     || biatNoiseHit
                     || bnaNoiseHit
                     || isRepeatedBoilerplate
                     || (isQnb && Regex.IsMatch(joined, @"Cette\s+d[ée]claration\s+sera\s+consid[ée]r[ée]e|dans\s+votre\s+situation\s+de\s+compte|Pour\s+toute\s+r[ée]clamation", RegexOptions.IgnoreCase));
                // Si la date est vide, tenter de la trouver dans le libellé (date 8 chiffres)
                if (string.IsNullOrEmpty(normalizedDate) && amountCandidates.Count > 0)
                {
                    var dateInLibelle = Regex.Match(joined, @"\b(\d{8})\b");
                    if (dateInLibelle.Success)
                        normalizedDate = NormalizeDate(dateInLibelle.Groups[1].Value, documentYear);
                }

                if (string.IsNullOrEmpty(normalizedDate))
                {
                    if (isNoise) continue;

                    bool isPureAmountLine = amountCandidates.Count > 0;

                    if (isPureAmountLine)
                    {
                        if (string.IsNullOrEmpty(pendingDate))
                        {
                            // [BTK] Une ligne de commission/TVA (ex: "COM & TVA RET. CARTE") suit
                            
                            if ((isBtk || isUbci || isBna) && current.Transactions.Count > 0)
                                pendingDate = current.Transactions[current.Transactions.Count - 1].Date;
                            else
                                continue;
                        }

                        string fullDesc = pendingLibelleBuffer.Trim();

                       
                        if (isAmenDocument || isBtk || isUbci || isBna)
                        {
                            string currentNonAmount = string.Join(" ", cellTexts.Where(c => !AmountRegex.IsMatch(c))).Trim();
                            currentNonAmount = Regex.Replace(currentNonAmount, @"\b\d{2}[/\-.]\d{2}[/\-.]\d{4}\b", "").Trim();
                            currentNonAmount = Regex.Replace(currentNonAmount, @"\b\d{8}\b", "").Trim();
                            if (!string.IsNullOrEmpty(currentNonAmount))
                                fullDesc = (fullDesc + " " + currentNonAmount).Trim();
                        }
                        pendingLibelleBuffer = "";

                        //update le 2 aout 2026 pour corriger le saut des lignes 
                        if (IsMergedRow(amountCandidates, debitAnchor, creditAnchor, soldeAnchor))
                        {
                            foreach (var splitTx in SplitMergedRow(pendingDate, fullDesc, amountCandidates, debitAnchor, creditAnchor))
                                current.Transactions.Add(splitTx);
                            pendingDate = "";
                            biatInNoiseZone = false;
                            continue;
                        }
                        decimal? soldeAvant2 = previousSolde;
                        var tx2 = new Transaction { Date = pendingDate, Libelle = fullDesc };
                        AssignAmounts(tx2, amountCandidates, debitAnchor, creditAnchor, soldeAnchor, montantAnchor, isBtk, ref previousSolde, hasSignedAmounts, out var soldeCourantTX);
                        ApplyMovementFallback(tx2, soldeAvant2, soldeCourantTX);
                        //if (isQnb) ApplyQnbSignRule(tx2);   // <-- AJOUTE CETTE LIGNE
                        current.Transactions.Add(tx2);
                        pendingDate = "";
                        biatInNoiseZone = false;
                        continue;
                    }

                    // Texte de continuation
                    if (amountCandidates.Count == 0)
                    {
                        if (current.Transactions.Count > 0 && string.IsNullOrEmpty(pendingDate))
                        {
                            var lastTx = current.Transactions[current.Transactions.Count - 1];
                      
                            if (!isNoise && !biatInNoiseZone && !(isQnb && Regex.IsMatch(joined, @"support|hotline|N\.B", RegexOptions.IgnoreCase)))
                                lastTx.Libelle = (lastTx.Libelle + " " + joined.Trim()).Trim();
                        }
                        else if (!biatInNoiseZone)
                        {
                            pendingLibelleBuffer = (pendingLibelleBuffer + " " + joined.Trim()).Trim();
                        }
                    }
                    continue;
                }

                // Ligne avec date valide : une vraie date marque le début d'une nouvelle
                // opération, donc la zone de bruit BIAT (s'il y en avait une) est terminée.
                biatInNoiseZone = false;

             
                if (amountCandidates.Count == 0)
                {
                   
                    if (cellTexts.Count == 1 && current.Transactions.Count > 0
                        && string.IsNullOrEmpty(pendingDate) && string.IsNullOrEmpty(pendingLibelleBuffer))
                    {
                        var lastTx = current.Transactions[current.Transactions.Count - 1];
                        if (!isNoise && !biatInNoiseZone)
                            lastTx.Libelle = (lastTx.Libelle + " " + joined.Trim()).Trim();
                    }
                    else
                    {
                       
                        if (!string.IsNullOrEmpty(pendingDate) && pendingDate != normalizedDate)
                        {
                            var orphanTx = new Transaction
                            {
                                Date = pendingDate,
                                Libelle = (pendingLibelleBuffer.Trim() + " [MONTANT MANQUANT - a verifier manuellement]").Trim()
                            };
                            current.Transactions.Add(orphanTx);
                            pendingLibelleBuffer = "";
                        }

                        pendingDate = normalizedDate;
                        string textOnly = string.Join(" ", cellTexts.Skip(1).Where(c => !AmountRegex.IsMatch(c)));
                        pendingLibelleBuffer = (pendingLibelleBuffer + " " + textOnly).Trim();
                    }
                    continue;
                }


                if (current != null)
            {
                // Cas normal : date + montant(s)
                string description = string.Join(" ", cellTexts.Skip(1).Where(c => !AmountRegex.IsMatch(c)))
                    .Trim(' ', '|', '[', ']', '-', '_');
                description = Regex.Replace(description, @"\b\d{2}[/\-.]\d{2}[/\-.]\d{4}\b", "").Trim();
                description = Regex.Replace(description, @"\b\d{8}\b", "").Trim();
                description = Regex.Replace(description, @"\s{2,}", " ").Trim();

                // [QNB] Ne pas garder les footers dans la description
                if (isQnb && Regex.IsMatch(description, @"support|hotline|N\.B|Pour toute|Cette déclaration", RegexOptions.IgnoreCase))
                    continue;

                string fullDescription = (pendingLibelleBuffer + " " + description).Trim();
                pendingLibelleBuffer = "";
                pendingDate = "";


                //update le 2 aout aussi meme raison
                if (IsMergedRow(amountCandidates, debitAnchor, creditAnchor, soldeAnchor))
                {
                    foreach (var splitTx in SplitMergedRow(normalizedDate, fullDescription, amountCandidates, debitAnchor, creditAnchor))
                        current.Transactions.Add(splitTx);
                    biatInNoiseZone = false;
                    continue;
                }

                decimal? soldeAvantTx = previousSolde;
                var tx = new Transaction { Date = normalizedDate, Libelle = fullDescription };
                AssignAmounts(tx, amountCandidates, debitAnchor, creditAnchor, soldeAnchor, montantAnchor, isBtk, ref previousSolde, hasSignedAmounts, out  var soldeCourantTx);
                ApplyMovementFallback(tx, soldeAvantTx, soldeCourantTx);
                //if (isQnb) ApplyQnbSignRule(tx);   // <-- AJOUTE CETTE LIGNE
                current.Transactions.Add(tx);
                biatInNoiseZone = false;
            }
            }

            
            if (current != null && !sections.Contains(current))
            {
                current.RawSectionText = sectionRawText;
                sections.Add(current);
            }

            // Calcul des totaux pour chaque section
            foreach (var sec in sections)
            {
                var totalDebitMatch = Regex.Match(sec.RawSectionText,
                    @"Total\s*(?:des\s*)?D[ée]bit(?:s)?\s*:?\s*(-?\d{1,3}(?:[ .,]\d{3})*[.,]\d{2,3})", RegexOptions.IgnoreCase);
                if (totalDebitMatch.Success) sec.TotalDebit = ParseAmount(totalDebitMatch.Groups[1].Value);

                var totalCreditMatch = Regex.Match(sec.RawSectionText,
                    @"Total\s*(?:des\s*)?Cr[ée]dit(?:s)?\s*:?\s*(-?\d{1,3}(?:[ .,]\d{3})*[.,]\d{2,3})", RegexOptions.IgnoreCase);
                if (totalCreditMatch.Success) sec.TotalCredit = ParseAmount(totalCreditMatch.Groups[1].Value);

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

        

        // ↓↓↓ NOUVEAU BLOC À INSÉRER ICI ↓↓↓
        private static readonly string[] BhKnownLibellePrefixes = {
              "COMFORC PRLV.", "VERSEMENT TPE", "VERS.CHQ.ORDIN", "VRST.AUT.AG.",
            "ENC.CHQ.TN", "ENC.EFFET TN", "Vers ESP RECU",
            "COMMISSION", "T.V.A", "PRLV.", "VRST.",

            };
        private string ExtractBhLibelle(string middle) {
            string stripped = middle.Trim();
            if (stripped.Length == 0) return stripped;

            foreach (var prefix in BhKnownLibellePrefixes)
            {
                if (stripped.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    return stripped.Substring(0, prefix.Length).Trim();
            }

            var ibMatch = Regex.Match(stripped, @"\bIB\b");
            if (ibMatch.Success && ibMatch.Index > 0)
                return stripped.Substring(0, ibMatch.Index).Trim();

            return stripped;
        }
        // ↑↑↑ FIN DU NOUVEAU BLOC ↑↑↑



        
        private (int? debit, int? credit) InferAnchorsFromAmountPositions(List<TableRow> rows)
        {
            var positions = new List<int>();
            foreach (var row in rows)
                foreach (var cell in row.Cells)
                    if (AmountRegex.IsMatch(cell.Text))
                        positions.Add(cell.Left);

            var sorted = positions.Distinct().OrderBy(x => x).ToList();
            if (sorted.Count < 2) return (null, null);

            int bestGapIndex = 0, bestGap = 0;
            for (int i = 0; i < sorted.Count - 1; i++)
            {
                int gap = sorted[i + 1] - sorted[i];
                if (gap > bestGap) { bestGap = gap; bestGapIndex = i; }
            }
            // Gap trop faible : les montants ne se separent pas clairement en 2 colonnes distinctes,
            // on renonce plutot que de deviner un mauvais decoupage.
            if (bestGap < 40) return (null, null);

            int leftCluster = (int)sorted.Take(bestGapIndex + 1).Average();
            int rightCluster = (int)sorted.Skip(bestGapIndex + 1).Average();
            return (leftCluster, rightCluster);
        }
        private List<BankAccountSection> ExtractBhAccountSections(string fullText)
        {
            var sections = new List<BankAccountSection>();
            string documentRib = ExtractRib(fullText);
            string accountNumber = ExtractAccountNumber(fullText);

            var current = new BankAccountSection
            {
                AccountNumber = accountNumber,
                Rib = documentRib,
                Currency = ExtractCurrency(fullText),
                SoldeInitial = null
            };

            var bhLineRegex = new Regex(
    @"^(?:\d+\s+)?(\d{2}/\d{2}/\d{4})\s+(.+?)\s+(?:(\d{2}/\d{2}/\d{4})\s+)?(-?\d[\d\s.,]*\d|\d)\s*$",
    RegexOptions.IgnoreCase);

            var soldeOuvertureRegex = new Regex(@"^(-?\d[\d\s.,]*\d|\d)\s*$");
            var soldeAuLabelRegex = new Regex(@"Solde\s+au\s+\d{2}/\d{2}/\d{4}", RegexOptions.IgnoreCase);

            var lines = fullText.Split('\n');

            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i].Trim();
                if (string.IsNullOrWhiteSpace(line)) continue;

                var soldeAmountMatch = soldeOuvertureRegex.Match(line);
                if (soldeAmountMatch.Success)
                {
                    string nextLine = (i + 1 < lines.Length) ? lines[i + 1].Trim() : "";
                    if (soldeAuLabelRegex.IsMatch(nextLine))
                    {
                        current.SoldeInitial = ParseAmount(soldeAmountMatch.Groups[1].Value);
                    }
                    continue;
                }

                if (soldeAuLabelRegex.IsMatch(line) && !Regex.IsMatch(line, @"^\d{2}/\d{2}/\d{4}"))
                    continue;

                if (Regex.IsMatch(line, @"^-?\d[\d\s.,]*\s+-?\d[\d\s.,]*$"))
                    continue;

                if (Regex.IsMatch(line, @"Total\s+des\s+mouvements|^Solde\s+au\s+\d{2}/\d{2}/\d{4}\s*:", RegexOptions.IgnoreCase))
                {
                    var soldeFinalMatch = AmountRegex.Match(line);
                    if (soldeFinalMatch.Success)
                        current.SoldeFinal = ParseAmount(soldeFinalMatch.Value);
                    continue;
                }

                if (Regex.IsMatch(line, @"^Date\s+op[ée]ration|N[o°]\s*du\s+compte|Titulaire\s+du\s+compte|Op[ée]rations\s+du|Extrait\s+de\s+Compte", RegexOptions.IgnoreCase))
                    continue;

                var match = bhLineRegex.Match(line);
                if (!match.Success)
                {
                    Console.WriteLine($"[BH-WARN] Ligne non reconnue (ignorée) : {line}");
                    continue;
                }

                string dateOp = match.Groups[1].Value;
                string libelle = ExtractBhLibelle(match.Groups[2].Value);
                decimal montant = ParseAmount(match.Groups[4].Value);

                var tx = new Transaction
                {
                    Date = NormalizeDate(dateOp, null),
                    Libelle = libelle
                };

                if (IsBhDebitLibelle(libelle))
                    tx.Debit = montant;
                else
                    tx.Credit = montant;

                current.Transactions.Add(tx);
            }

            current.RawSectionText = fullText;
            sections.Add(current);
            return sections;
        }

        private string NormalizeDate(string raw, int? defaultYear = null)
        {
            raw = raw.Trim();
            string candidate = raw;

            if (Regex.IsMatch(raw, @"^\d{1,2}\s+\d{1,2}\s*$") && !raw.Contains('/') && !raw.Contains('-'))
            {
                candidate = Regex.Replace(raw.Trim(), @"\s+", "/");
            }

            if (raw.Length == 8 && !raw.Contains('/') && !raw.Contains('-') && !raw.Contains('.'))
                candidate = $"{raw.Substring(0, 2)}/{raw.Substring(2, 2)}/{raw.Substring(4, 4)}";
            else if (raw.Length == 4 && !raw.Contains('/') && !raw.Contains('-') && !raw.Contains('.') && !raw.Contains(' '))
                candidate = $"{raw.Substring(0, 2)}/{raw.Substring(2, 2)}";

        
            var formatsWithYear = new[]
            {
                "dd/MM/yyyy", "dd-MM-yyyy", "dd.MM.yyyy", "yyyy-MM-dd", "yyyy/MM/dd",
                "dd/MM/yy", "dd-MM-yy", "dd.MM.yy",
                "dd MMM yyyy", "dd MMM yy", "dd-MMM-yyyy", "dd-MMM-yy", "dd.MMM.yyyy", "dd.MMM.yy",
            };

            if (DateTime.TryParseExact(candidate, formatsWithYear, CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var parsed))
            {
                return parsed.ToString("dd/MM/yyyy");
            }

            var formatsNoYear = new[] { "dd/MM", "dd-MM", "dd.MM", "dd MM" };
            if (DateTime.TryParseExact(candidate, formatsNoYear, CultureInfo.InvariantCulture,
                    DateTimeStyles.NoCurrentDateDefault, out var parsedNoYear))
            {
                int year = defaultYear ?? DateTime.Now.Year;
                var withYear = new DateTime(year, parsedNoYear.Month, parsedNoYear.Day);
                return withYear.ToString("dd/MM/yyyy");
            }

            return "";
        }

        private string GetNormalizedDateFromCells(List<string> cellTexts, int? defaultYear = null)
        {
            for (int i = 0; i < Math.Min(cellTexts.Count, 3); i++)
            {
                string candidate = string.Join(" ", cellTexts.Take(i + 1));
                string normalized = NormalizeDate(candidate, defaultYear);
                if (!string.IsNullOrEmpty(normalized))
                    return normalized;
            }
            return "";
        }
        private string GetDateFromAnyCell(List<string> cellTexts, int? defaultYear = null)
        {
            foreach (var cell in cellTexts)
            {
                string normalized = NormalizeDate(cell.Trim(), defaultYear);
                if (!string.IsNullOrEmpty(normalized))
                    return normalized;
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
            var patterns = new[]
            {
                @"Num[ée]ro\s*de\s*Compte\s*:?\s*(\d[\d\s-]{6,25})",   // <-- AJOUTE CETTE LIGNE
                @"\b\d{2,5}-\d{4,12}-\d{1,4}\b",
                @"\b\d{10,20}\b",
                @"Compte\s*:?\s*(\d[\d\s-]{8,25})",
                @"Account\s*Number\s*:?\s*(\d[\d\s-]{8,25})",
                @"N[°o]?\s*Compte\s*:?\s*(\d[\d\s-]{8,25})"
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
            if (wide.Success)
                return Regex.Replace(wide.Value, @"\s+", "").Trim();

            var narrow = Regex.Match(text, @"TN\d{2}[\s\d]{15,25}");
            if (narrow.Success)
                return Regex.Replace(narrow.Value, @"\s+", "").Trim();

            return "";
        }

        private bool IsMergedRow(dynamic amountCandidates, int? debitAnchor, int? creditAnchor, int? soldeAnchor)
        {
            if (!debitAnchor.HasValue || !creditAnchor.HasValue) return false;

            int debitCount = 0, creditCount = 0;
            foreach (var cand in amountCandidates)
            {
                int distDebit = Math.Abs((int)cand.Left - debitAnchor.Value);
                int distCredit = Math.Abs((int)cand.Left - creditAnchor.Value);
                int distSolde = soldeAnchor.HasValue ? Math.Abs((int)cand.Left - soldeAnchor.Value) : int.MaxValue;
                if (soldeAnchor.HasValue && distSolde <= distDebit && distSolde <= distCredit) continue;
                if (distDebit < distCredit) debitCount++; else creditCount++;
            }
            return debitCount > 1 || creditCount > 1;
        }

        private List<Transaction> SplitMergedRow(string date, string libelle, dynamic amountCandidates, int? debitAnchor, int? creditAnchor)
        {
            var debits = new List<decimal>();
            var credits = new List<decimal>();
            foreach (var cand in amountCandidates)
            {
                int distDebit = Math.Abs((int)cand.Left - debitAnchor.Value);
                int distCredit = Math.Abs((int)cand.Left - creditAnchor.Value);
                if (distDebit < distCredit) debits.Add(cand.Value); else credits.Add(cand.Value);
            }

            int count = Math.Max(debits.Count, credits.Count);
            var result = new List<Transaction>();
            for (int i = 0; i < count; i++)
            {
                var tx = new Transaction
                {
                    Date = date,
                    Libelle = $"{libelle} [LIGNE FUSIONNEE {i + 1}/{count} - a verifier manuellement]".Trim()
                };
                if (i < debits.Count) tx.Debit = debits[i];
                if (i < credits.Count) tx.Credit = credits[i];
                result.Add(tx);
            }
            return result;
        }
        private List<BankAccountSection> ExtractUbciAccountSections(List<TableRow> rows, string fullText)
        {
            var sections = new List<BankAccountSection>();
            string documentRib = ExtractRib(fullText);
            string accountNumber = ExtractAccountNumber(fullText);

            // [UBCI] Le document contient souvent chaque page en double (rendu image + couche
 
            var seenRowKeys = new HashSet<string>();
            var dedupedRows = new List<TableRow>();
            foreach (var row in rows)
            {
                string rowKey = string.Join("|", row.Cells.Select(c => CleanWhitespace(c.Text).Trim()));
                if (string.IsNullOrWhiteSpace(rowKey)) continue;
                if (!seenRowKeys.Add(rowKey)) continue;
                dedupedRows.Add(row);
            }

            int? debitAnchor = null, creditAnchor = null;

            var current = new BankAccountSection
            {
                AccountNumber = accountNumber,
                Rib = documentRib,
                Currency = "TND",
                SoldeInitial = null
            };

            string pendingDate = "";
            string pendingLibelle = "";

            foreach (var row in dedupedRows)
            {
                var cells = row.Cells.OrderBy(c => c.Left).ToList();
                var texts = cells.Select(c => CleanWhitespace(c.Text).Trim()).ToList();
                string joined = string.Join(" ", texts);
                if (string.IsNullOrWhiteSpace(joined)) continue;

                // Solde d'ouverture : "SOLDE DEBITEUR AU 01/02/2025 -20.080,474"
                var openMatch = Regex.Match(joined,
                    @"SOLDE\s+(DEBITEUR|CREDITEUR)\s+AU\s+\d{2}/\d{2}/\d{4}\s+(-?\d{1,3}(?:[ .,]\d{3})*[.,]\d{2,3})",
                    RegexOptions.IgnoreCase);
                if (openMatch.Success)
                {
                    decimal init = ParseAmount(openMatch.Groups[2].Value);
                    init = openMatch.Groups[1].Value.Equals("DEBITEUR", StringComparison.OrdinalIgnoreCase)
                        ? -Math.Abs(init) : Math.Abs(init);
                    current.SoldeInitial = init;
                    continue;
                }

                // En-tête de colonnes ("Débit", "Crédit"...) : capture les ancres, ligne ignorée
                var debitCell = cells.FirstOrDefault(c => Regex.IsMatch(c.Text, @"^D[ée]bit$", RegexOptions.IgnoreCase));
                var creditCell = cells.FirstOrDefault(c => Regex.IsMatch(c.Text, @"^Cr[ée]dit$", RegexOptions.IgnoreCase));
                if (debitCell != null || creditCell != null)
                {
                    if (debitCell != null) debitAnchor = debitCell.Left;
                    if (creditCell != null) creditAnchor = creditCell.Left;
                    continue;
                }

                // Pied de page : totaux + solde de clôture
                if (Regex.IsMatch(joined, @"TOTAL\s+DU\s+DEBIT\s+ET\s+DU\s+CREDIT", RegexOptions.IgnoreCase))
                {
                    var amounts = AmountRegex.Matches(joined);
                    if (amounts.Count >= 2)
                    {
                        current.TotalDebit = ParseAmount(amounts[0].Value);
                        current.TotalCredit = ParseAmount(amounts[1].Value);
                    }
                    continue;
                }
                if (Regex.IsMatch(joined, @"SOLDE\s+DE\s+CLOTURE", RegexOptions.IgnoreCase))
                {
                    var m = AmountRegex.Match(joined);
                    if (m.Success) current.SoldeFinal = ParseAmount(m.Value);
                    continue;
                }
                if (Regex.IsMatch(joined, @"Cette\s+op[ée]ration\s+est\s+provisoire|Page\s*\d|^R\.?\s*N\.?\s*E|SWIFT", RegexOptions.IgnoreCase))
                    continue;

                
                var dateAtStart = Regex.Match(joined, @"^(\d{2}/\d{2}/\d{4})\s+(.+)$");

                // [UBCI] Un montant est "dans une colonne Débit/Crédit" seulement s'il est ancré
                // à droite (position Left >= ancre connue - tolérance). Un montant dans le libellé
                // (partie gauche de la ligne) ne doit PAS bloquer le traitement date+libellé.
                bool lineHasColumnAmount = false;
                if (debitAnchor.HasValue || creditAnchor.HasValue)
                {
                    lineHasColumnAmount = cells.Any(c =>
                        AmountRegex.IsMatch(c.Text) &&
                        ((debitAnchor.HasValue && Math.Abs(c.Left - debitAnchor.Value) < 60) ||
                         (creditAnchor.HasValue && Math.Abs(c.Left - creditAnchor.Value) < 60)));
                }
                else
                {
                    // Ancres pas encore vues : repli conservateur — si la DERNIERE cellule porte
                    // un montant, c'est probablement une colonne Débit/Crédit.
                    var lastCell = cells.LastOrDefault();
                    lineHasColumnAmount = lastCell != null && AmountRegex.IsMatch(lastCell.Text);
                }

                if (dateAtStart.Success && !lineHasColumnAmount)
                {
                    if (!string.IsNullOrEmpty(pendingDate))
                    {
                        current.Transactions.Add(new Transaction
                        {
                            Date = pendingDate,
                            Libelle = (pendingLibelle + " [MONTANT MANQUANT - a verifier manuellement]").Trim()
                        });
                    }
                    pendingDate = dateAtStart.Groups[1].Value;
                    pendingLibelle = dateAtStart.Groups[2].Value.Trim();
                    continue;
                }

                
                TableCell amountCell = null;
                if (debitAnchor.HasValue || creditAnchor.HasValue)
                {
                    amountCell = cells.FirstOrDefault(c =>
                        AmountRegex.IsMatch(c.Text) &&
                        ((debitAnchor.HasValue && Math.Abs(c.Left - debitAnchor.Value) < 60) ||
                         (creditAnchor.HasValue && Math.Abs(c.Left - creditAnchor.Value) < 60)));
                }
                else
                {
                    // Repli : dernière cellule avec montant (comportement original)
                    amountCell = cells.LastOrDefault(c => AmountRegex.IsMatch(c.Text));
                }
                if (amountCell != null && !string.IsNullOrEmpty(pendingDate))


                {
                    decimal montant = ParseAmount(AmountRegex.Match(amountCell.Text).Value);
                    var tx = new Transaction { Date = pendingDate, Libelle = pendingLibelle };

                    if (debitAnchor.HasValue && creditAnchor.HasValue)
                    {
                        int distDebit = Math.Abs(amountCell.Left - debitAnchor.Value);
                        int distCredit = Math.Abs(amountCell.Left - creditAnchor.Value);
                        if (distDebit < distCredit) tx.Debit = montant; else tx.Credit = montant;
                    }
                    else
                    {
                        // Repli si les ancres n'ont pas été vues sur cette page (rare avec la
                        // tolérance verticale activée, mais on ne veut jamais perdre le montant)
                        tx.Debit = montant;
                    }

                    current.Transactions.Add(tx);
                    pendingDate = "";
                    pendingLibelle = "";
                    continue;
                }

                // Texte de continuation : le libellé déborde sur une 2e/3e ligne physique
                if (!string.IsNullOrEmpty(pendingDate))
                    pendingLibelle = (pendingLibelle + " " + joined).Trim();
            }

            if (!string.IsNullOrEmpty(pendingDate))
            {
                current.Transactions.Add(new Transaction
                {
                    Date = pendingDate,
                    Libelle = (pendingLibelle + " [MONTANT MANQUANT - a verifier manuellement]").Trim()
                });
            }

            current.RawSectionText = fullText;
            sections.Add(current);
            return sections;
        }

        private void AssignAmounts(Transaction tx, dynamic amountCandidates, int? debitAnchor, int? creditAnchor, int? soldeAnchor, int? montantAnchor, bool isBtk, ref decimal? previousSolde , bool hasSignedAmounts, out decimal? soldeCourant)
        {
            const int Tolerance = 15;

            soldeCourant = null;   // remplace tx.Solde

            if (debitAnchor.HasValue && creditAnchor.HasValue)
            {
                var ambiguous = new List<dynamic>();

                // [BTK] Chaque ligne BTK n'affiche que Montant (Débit OU Crédit) + Solde,
              
                dynamic classifiable = amountCandidates;
                if (isBtk && amountCandidates.Count > 1)
                {
                    var trimmed = new List<dynamic>();
                    foreach (var c in amountCandidates) trimmed.Add(c);
                    soldeCourant = trimmed[trimmed.Count - 1].Value;
                    trimmed.RemoveAt(trimmed.Count - 1);
                    classifiable = trimmed;
                }

                foreach (var cand in classifiable)
                {
                    int distDebit = Math.Abs((int)cand.Left - debitAnchor.Value);
                    int distCredit = Math.Abs((int)cand.Left - creditAnchor.Value);
                    int distSolde = soldeAnchor.HasValue ? Math.Abs((int)cand.Left - soldeAnchor.Value) : int.MaxValue;

                    if (!isBtk && distSolde <= distDebit && distSolde <= distCredit)
                    {
                        soldeCourant = cand.Value;
                    }
                    // [FAMILLE MONTANTS SIGNES] QNB, BIAT extrait, et tout document ou hasSignedAmounts==true :
                    
                    else if (hasSignedAmounts)
                    {
                        if (cand.Value < 0) tx.Debit = Math.Abs(cand.Value);
                        else if (cand.Value > 0) tx.Credit = cand.Value;
                    }
                    else if (Math.Abs(distDebit - distCredit) < Tolerance)
                    {
                        ambiguous.Add(cand);
                    }
                    else if (distDebit < distCredit)
                    {
                        tx.Debit = Math.Abs(cand.Value);
                    }
                    else
                    {
                        tx.Credit = Math.Abs(cand.Value);
                    }
                }

                if (!isBtk && !soldeCourant.HasValue && soldeAnchor.HasValue && amountCandidates.Count > 0)
                    soldeCourant = amountCandidates[amountCandidates.Count - 1].Value;

                foreach (var cand in ambiguous)
                {
                    decimal value = cand.Value;
                    if (previousSolde.HasValue && soldeCourant.HasValue)
                    {
                        if (soldeCourant.Value < previousSolde.Value) tx.Debit = value;
                        else if (soldeCourant.Value > previousSolde.Value) tx.Credit = value;
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
                    decimal val = amounts[0];
                    if (val < 0) tx.Debit = Math.Abs(val);
                    else if (val > 0) tx.Credit = val;
                    soldeCourant = null;// reste null si pas de solde courant
                  
                }
                
                else
                {
                    decimal mouvement = amounts[0];
                    decimal solde = amounts[amounts.Count - 1];
                    soldeCourant = solde;
                    if (previousSolde.HasValue)
                    {
                        if (solde < previousSolde.Value) tx.Debit = mouvement;
                        else if (solde > previousSolde.Value) tx.Credit = mouvement;
                    }
                    else tx.Debit = mouvement;
                }
            }
            previousSolde = soldeCourant;
        }

        private void ApplyMovementFallback(Transaction tx, decimal? soldeAvant , decimal? soldeCourant)
        {
            if (tx.Debit.HasValue || tx.Credit.HasValue) return;
            if (!soldeAvant.HasValue || !soldeCourant.HasValue) return;

            decimal diff = soldeCourant.Value - soldeAvant.Value;
            if (diff < 0) tx.Debit = Math.Abs(diff);
            else if (diff > 0) tx.Credit = diff;
        }

        // [QNB uniquement] Le signe du montant determine Debit/Credit de façon fiable :
        // negatif = Debit, positif = Credit. Ne s'applique qu'aux documents QNB.
        private void ApplyQnbSignRule(Transaction tx)
        {
            decimal? val = tx.Debit ?? tx.Credit;
            if (!val.HasValue) return;

            decimal abs = Math.Abs(val.Value);
            if (val.Value < 0)
            {
                tx.Debit = abs;
                tx.Credit = null;
            }
            else if (val.Value > 0)
            {
                tx.Credit = abs;
                tx.Debit = null;
            }
        }
        private bool IsDuplicateOfLast(BankAccountSection section, Transaction tx)
        {
            if (section.Transactions.Count == 0) return false;
            var last = section.Transactions[section.Transactions.Count - 1];
            return last.Date == tx.Date
                && last.Libelle == tx.Libelle
                && last.Debit == tx.Debit
                && last.Credit == tx.Credit; 
               
        }

        // [BIAT] Bruit d'en-tête/pied de page répété sur chaque page (titulaire du compte,
     
        private static bool IsBiatNoise(string text)
        {
            if (ArabicScriptRegex.IsMatch(text)) return true;
            return Regex.IsMatch(text,
                
                @"Titulaire\s+du\s+compte|N[°o]?\s*de\s+compte|\bRIB\b|Cher\s+client|Agence\s*:\s*Mr\b|Fonds\s+de\s+Garantie|RELEVE\s+.*MENSUEL|BANQUE\s+INTERNATIONALE\s+ARABE|TOTAUX|Nous\s+avons\s+l.honneur|Nous\s+vous\s+prions\s+de\s+contacter",
                RegexOptions.IgnoreCase);
        }

        // [BIAT] "عليه"/"له" sont des mots arabes très courants qui apparaissent aussi dans les
       
        private static bool IsBiatArabicHeaderCell(string text, string keyword)
        {
            string trimmed = text.Trim().Trim('‏', '‎', '.', ':', '»', '«', '،', ' ');
            return trimmed.Length > 0 && trimmed.Length <= 8 && trimmed.Contains(keyword);
        }

     
        private static readonly string[] BhDebitKeywords =
        {
            "PRLV.", "COMMISSION", "T.V.A", "COMFORC", "VIR.TN MM BQ"
        };

        private bool IsBhDebitLibelle(string libelle)
        {

            // Nettoie le bruit OCR devant le mot-clé (_, |, [, ], espaces, tirets, points isolés)
            string cleaned = libelle.TrimStart('_', '|', '[', ']', ' ', '-', '.', '\t');

            foreach (var kw in BhDebitKeywords)
                if (cleaned.StartsWith(kw, StringComparison.OrdinalIgnoreCase))
                    return true;
            return false;
        }

        private string ExtractCurrency(string text)
        {
            if (text.Contains("TND")) return "TND";
            if (text.Contains("DINAR")) return "TND";
            if (text.Contains("EUR")) return "EUR";
            if (text.Contains("USD")) return "USD";
            return "";
        }

        private string CleanWhitespace(string text) =>
     Regex.Replace(
         text.Replace('\u00A0', ' ')
             .Replace('\u202F', ' ')
             .Replace('\u2212', '-')
             .Replace('\r', ' ')
             .Replace('\n', ' '),
             @"\s+", " ").Trim();

        private string NormalizeSignSpacing(string text) =>
            Regex.Replace(text, @"^(\s*-)\s+(?=\d)", "-");

        private string StripPrintArtifacts(string text)
        {
            var urlMatch = Regex.Match(text, @"https?://\S+", RegexOptions.IgnoreCase);
            if (urlMatch.Success)
                text = text.Substring(0, urlMatch.Index);

            text = Regex.Replace(text, @"\bBTK@?DIRECT\b", "", RegexOptions.IgnoreCase);
            text = Regex.Replace(text, @"\b\d{1,2}/\d{1,2}/\d{2,4},?\s*\d{1,2}:\d{2}\s*(AM|PM)\b", "", RegexOptions.IgnoreCase);

            return CleanWhitespace(text).Trim();
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
                ("AMEN BANK", "Amen Bank"),
                ("WIFAK", "Wifak Bank"),                      // ← AJOUT
                ("BH", "Banque de l'Habitat (BH)"),
                ("BTK", "Banque Tuniso-Koweitienne (BTK)")   // ← ajouter
            };

            foreach (var bank in knownBanks)
                if (text.Contains(bank.Keyword))
                    return bank.FullName;

            return "";
        }
    }
}