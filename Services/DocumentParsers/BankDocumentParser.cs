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
        new(@"-?\d{1,3}(?:[ .,]?\d{3})*[.,]\d{2,3}");


        private static readonly Regex ArabicScriptRegex = new("[؀-ۿ]");

       
        private static readonly Regex AmenEchoDateRegex =
            new(@"\bDU\s+\d{2}\.\d{2}\.\d{2,4}\b", RegexOptions.IgnoreCase);

        private static string StripAmenEchoDate(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;
            string cleaned = AmenEchoDateRegex.Replace(text, "").Trim();
            return Regex.Replace(cleaned, @"\s{2,}", " ");
        }

        private static readonly Regex BnaReleveValeurDateRegex =
            new(@"\b\d{1,2}\s+\d{1,2}\s+\d{4}\b");

        private static string StripBnaReleveValeurDate(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;
            string cleaned = BnaReleveValeurDateRegex.Replace(text, "").Trim();
            return Regex.Replace(cleaned, @"\s{2,}", " ");
        }

        
        private static readonly Regex BnaRateLabelRegex =
            new(@"\bTEG\s*\d+(?:[.,]\d+)?", RegexOptions.IgnoreCase);

        private static string StripBnaRateLabel(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;
            string cleaned = BnaRateLabelRegex.Replace(text, "").Trim();
            return Regex.Replace(cleaned, @"\s{2,}", " ");
        }

     
        private static readonly Regex BnaFooterBoilerplateRegex =
            new(@"\bBNA\s+H24\b.*$|\bTOUTE\s+ERREUR\s+OU\s+OMISSION\b.*$", RegexOptions.IgnoreCase | RegexOptions.Singleline);

        private static string StripBnaFooterBoilerplate(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;
            string cleaned = BnaFooterBoilerplateRegex.Replace(text, "").Trim();
            return Regex.Replace(cleaned, @"\s{2,}", " ");
        }

        private static readonly Regex AlBarakaFooterBoilerplateRegex =
            new(@"\bCet\s+extrait\s+est\s+consid[ée]r[ée]\b.*$|\bCompliments\s+www\.albarakabank\b.*$", RegexOptions.IgnoreCase | RegexOptions.Singleline);

        private static string StripAlBarakaFooterBoilerplate(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;
            string cleaned = AlBarakaFooterBoilerplateRegex.Replace(text, "").Trim();
            return Regex.Replace(cleaned, @"\s{2,}", " ");
        }

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
            bool isAttijari = fullText.Contains("ATTIJARI", StringComparison.OrdinalIgnoreCase);
            bool isAbcBankDoc = fullText.Contains("Bank ABC", StringComparison.OrdinalIgnoreCase)
            || fullText.Contains("BANK ABC", StringComparison.OrdinalIgnoreCase);


            bool isBtk = fullText.Contains("BTK", StringComparison.OrdinalIgnoreCase);
            bool isBna = fullText.Contains("BNA", StringComparison.OrdinalIgnoreCase);
            bool isWifak = fullText.Contains("WIFAK", StringComparison.OrdinalIgnoreCase);
            bool isAtb = fullText.Contains("ATB", StringComparison.OrdinalIgnoreCase)
            || fullText.Contains("Arab Tunisian Bank", StringComparison.OrdinalIgnoreCase);
            bool isUbci = fullText.Contains("UBCI", StringComparison.OrdinalIgnoreCase)
                || Regex.IsMatch(fullText, @"UNION\s+BANCAIRE\s+POUR\s+LE\s+COMMERCE\s+ET\s+L['’]?\s*INDUSTRIE", RegexOptions.IgnoreCase);
           
            bool bteNameHit = (Regex.IsMatch(fullText, @"\bBTE\b", RegexOptions.IgnoreCase)
                    && !fullText.Contains("BTK", StringComparison.OrdinalIgnoreCase))
                || fullText.Contains("Banque de Tunisie et des Emirats", StringComparison.OrdinalIgnoreCase);

           
            int bteColumnHits =
                  (Regex.IsMatch(fullText, @"D\.?\s*Op[ée]\.?", RegexOptions.IgnoreCase) ? 1 : 0)
                + (Regex.IsMatch(fullText, @"(D\.?\s*Valeur|Date\s*de\s*Valeur)", RegexOptions.IgnoreCase) ? 1 : 0)
                + (Regex.IsMatch(fullText, @"\bD[ée]bit\b", RegexOptions.IgnoreCase) ? 1 : 0)
                + (Regex.IsMatch(fullText, @"\bCr[ée]dit\b", RegexOptions.IgnoreCase) ? 1 : 0);
            bool bteColumnSignature = bteColumnHits >= 3;

            bool bteDocumentTitleHit = Regex.IsMatch(fullText, @"Extrait\s+de\s+Compte", RegexOptions.IgnoreCase)
                || Regex.IsMatch(fullText, @"Relev[ée]\s+de\s+Compte", RegexOptions.IgnoreCase);


            bool bteStructuralFallback = !bteNameHit
                && bteColumnHits >= 4
                && bteDocumentTitleHit
                && !isBiat && !isZitouna && !isBtk && !isBna && !isWifak && !isAtb && !isUbci && !isAbcBankDoc
                && fullText.IndexOf("AMEN", StringComparison.OrdinalIgnoreCase) < 0
                && fullText.IndexOf("STB", StringComparison.OrdinalIgnoreCase) < 0
                && fullText.IndexOf("UIB", StringComparison.OrdinalIgnoreCase) < 0
                && fullText.IndexOf("ATTIJARI", StringComparison.OrdinalIgnoreCase) < 0
                && fullText.IndexOf("ALBARAKA", StringComparison.OrdinalIgnoreCase) < 0
                && fullText.IndexOf("albarakabank", StringComparison.OrdinalIgnoreCase) < 0;

            bool isBte = fullText.Contains("Banque de Tunisie et des Emirats", StringComparison.OrdinalIgnoreCase)
                || (bteNameHit && bteColumnSignature)
                || (bteNameHit && bteDocumentTitleHit && bteColumnHits >= 2)
                || bteStructuralFallback;

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
            if ((isBiat || isZitouna || isBtk || isBna || isBh || isWifak || isUbci || isBte || isAttijari) && !isAbcBankDoc) engine.VerticalTolerance = 1;

            var rows = engine.BuildTable(lines);
            rows = SplitDuplicatedRows(rows);
            if (isAttijari) rows = ReattachAttijariOrphanLabel(rows);
            if (!isBiat) rows = MergeContinuationLines(rows);
            if (isBtk)
            {
                foreach (var r in rows)
                    Console.WriteLine("[BTK-ROWS] " + string.Join(" ~~ ", r.Cells.Select(c => $"[{(c.IsContinuationDetail ? "CONT" : "MAIN")}:{c.Left}]'{c.Text}'")));
            }
            if (isUbci)
            {
                var ubciDocument = new BankDocument
                {
                    BankName = "Union Bancaire pour le Commerce et l'Industrie (UBCI)",
                    Accounts = ExtractUbciAccountSections(rows, fullText)
                };
                return ubciDocument;
            }
            if (isBte)
            {
                var bteDocument = new BankDocument
                {
                    BankName = "Banque de Tunisie et des Emirats (BTE)",
                    Accounts = ExtractBteAccountSections(rows, fullText)
                };
                return bteDocument;
            }

            var document = new BankDocument
            {
                BankName = bankName,
                Accounts = ExtractAccountSections(rows, fullText)
            };

            return document;
        }
        private enum LineCategory
        {
            TransactionStart, TransactionContinuation, TableHeader,
            AccountMetadata, Total, Balance, DocumentFooter, Noise
        }

        
        private static readonly Regex ClassifyDateAnywhereRegex =
            new(@"(?<!\d)\d{1,2}[\s/.\-]\d{1,2}[\s/.\-]\d{2,4}(?!\d)");

        private static readonly Regex ClassifyAmountAnywhereRegex =
            new(@"-?\d{1,3}(?:[ .,]?\d{3})*[.,]\d{1,3}");


        private static readonly Regex LeadingDigitRegex = new(@"^\d");

        private static readonly Regex LibelleHeaderFooterNoiseRegex = new(
            @"Compte\s*N[°o]|Num[ée]ro\s*(?:de\s*)?compte|^\s*RIB\b|IBAN\b|Adresse\s*:|Agence\s*(:|N[°o])|" +
            @"Intitul[ée]\s*:|Devise\s*(du\s*compte)?\s*:|Ville\s*:|P[ée]riode\s*:|Titulaire|Unit[ée]\s*:|" +
            @"Date\s+de\s+[lI1]['’]op[ée]ration\b|Relev[ée]\s+de\s+Compte|Extrait\s+de\s+Compte|SWIFT|BIC\b|" +
            @"sauf\s*erreur|sauf\s+avis\s+contraire|d[ée]lai\s+de\s+r[ée]clamation|jours?\s+[àa]\s+compter|" +
            @"jours?\s+suivant\s+la\s+date|tacitement|nos\s+[ée]critures|en\s+cas\s+de\s+contestation|" +
            @"si[èe]ge\s+social|m[ée]diateur|Boi?te\s+Postale|" +
            @"Cette\s+d[ée]claration\s+sera\s+consid[ée]r[ée]e|Cher\s+Client\b|" +
            @"Division\s+des\s+R[ée]clamations|" +
            @"(D[ée]bit.{0,20}Cr[ée]dit)|(Cr[ée]dit.{0,20}D[ée]bit)|D\.?\s*Op[ée]\.?\s+Libell[ée]|" +
            @"^[lI1]['’]op[ée]ration\.?\s*$|" +
            @"^\d{2}[/.\-]\d{2}[/.\-]\d{2,4}\s+\d{2}[/.\-]\d{2}[/.\-]\d{2,4}\s*-?\s*(Date\s+de)?\s*$",
            RegexOptions.IgnoreCase);

        private LineCategory ClassifyLine(TableRow row, bool hasOpenTransaction)
        {
            var orderedCells = row.Cells.OrderBy(c => c.Left).ToList();
            string joined = string.Join(" ", orderedCells.Select(c => c.Text)).Trim();
            if (string.IsNullOrWhiteSpace(joined)) return LineCategory.Noise;

            if (Regex.IsMatch(joined, @"(D[ée]bit.{0,20}Cr[ée]dit)|(Cr[ée]dit.{0,20}D[ée]bit)|D\.?\s*Op[ée]\.?\s+Libell[ée]|^Page\s*\d|^Folio\s*\d+\s*$|^\d+\s*/\s*\d+\s*$", RegexOptions.IgnoreCase))
                return LineCategory.TableHeader;

            if (Regex.IsMatch(joined, @"Solde\s*(au|initial|final|pr[ée]c[ée]dent|d['’]ouverture|de\s*cl[ôo]ture)", RegexOptions.IgnoreCase))
                return LineCategory.Balance;

            if (Regex.IsMatch(joined, @"Totaux?\b|Encours|\bReport\b|\bA\s+[Rr]eporter\b", RegexOptions.IgnoreCase))
                return LineCategory.Total;

            if (Regex.IsMatch(joined, @"sauf\s+erreur|d[ée]lai\s+de\s+r[ée]clamation|jours?\s+[àa]\s+compter|tacitement|nos\s+[ée]critures|en\s+cas\s+de\s+contestation|si[èe]ge\s+social", RegexOptions.IgnoreCase))
                return LineCategory.DocumentFooter;

            if (Regex.IsMatch(joined, @"Compte\s*N[°o]|^\s*RIB\b|IBAN\b|Adresse\s*:|Agence\s*:|Intitul[ée]\s*:|Devise\s*:|Ville\s*:|P[ée]riode\s*:|Titulaire", RegexOptions.IgnoreCase))
                return LineCategory.AccountMetadata;

            if (Regex.IsMatch(joined, @"^\d{2}[/.\-]\d{2}[/.\-]\d{2,4}\s+\d{2}[/.\-]\d{2}[/.\-]\d{2,4}\s*-?\s*(Date\s+de)?\s*$", RegexOptions.IgnoreCase))
                return LineCategory.AccountMetadata;

            if (Regex.IsMatch(joined, @"^Printed\s+on\s+\d{2}[/.\-]\d{2}[/.\-]\d{2,4}.*Page\s*\d+\s*/\s*\d+", RegexOptions.IgnoreCase))
                return LineCategory.DocumentFooter;

            bool hasDate = ClassifyDateAnywhereRegex.IsMatch(joined);
            bool hasAmount = ClassifyAmountAnywhereRegex.IsMatch(joined);
            bool hasLeadingDigit = orderedCells.Count > 0 && LeadingDigitRegex.IsMatch(orderedCells[0].Text.TrimStart());

            if (hasDate && hasAmount)
                return LineCategory.TransactionStart;

            if (hasLeadingDigit && (hasDate || hasAmount))
                return LineCategory.TransactionStart;

            if (!hasOpenTransaction)
                return (hasDate || hasAmount) ? LineCategory.TransactionStart : LineCategory.Noise;

          
            return LineCategory.TransactionContinuation;
        }
        private List<TableRow> MergeContinuationLines(List<TableRow> rows)
        {
            var result = new List<TableRow>();
            int? openTxIndex = null;

            foreach (var row in rows)
            {
                string joined = string.Join(" ", row.Cells.Select(c => c.Text)).Trim();
                var category = ClassifyLine(row, openTxIndex.HasValue);

                switch (category)
                {
                    case LineCategory.TransactionContinuation:
                        var lastCell = result[openTxIndex!.Value].Cells.LastOrDefault();
                        int left = lastCell != null ? lastCell.Left + 1 : 0;
                        
                        result[openTxIndex.Value].Cells.Add(new TableCell { Text = joined, Left = left, IsContinuationDetail = true });
                        break;

                    case LineCategory.TransactionStart:
                        result.Add(row);
                        openTxIndex = result.Count - 1;
                        break;

                    default:
                        result.Add(row);
                        openTxIndex = null;
                        break;
                }
            }
            return result;
        }

        private static readonly Regex AttijariOrphanLabelRegex = new(@"^(COMMISSION\b|VIR\s+RECU\b)", RegexOptions.IgnoreCase);
        private static readonly Regex TableCellDateRegex = new(@"^\d{2}[/.\-]\d{2}[/.\-]\d{2,4}$");

        private List<TableRow> ReattachAttijariOrphanLabel(List<TableRow> rows)
        {
            var result = new List<TableRow>();
            for (int i = 0; i < rows.Count; i++)
            {
                string joined = string.Join(" ", rows[i].Cells.Select(c => c.Text)).Trim();
                bool hasDate = ClassifyDateAnywhereRegex.IsMatch(joined);
                bool hasAmount = ClassifyAmountAnywhereRegex.IsMatch(joined);

                if (!hasDate && !hasAmount && AttijariOrphanLabelRegex.IsMatch(joined) && i + 1 < rows.Count)
                {
                    var nextRow = rows[i + 1];
                    string nextJoined = string.Join(" ", nextRow.Cells.Select(c => c.Text)).Trim();
                    if (ClassifyDateAnywhereRegex.IsMatch(nextJoined) && ClassifyAmountAnywhereRegex.IsMatch(nextJoined))
                    {
                        var nextNonDateLefts = nextRow.Cells
                            .Where(c => !TableCellDateRegex.IsMatch(c.Text.Trim()))
                            .Select(c => c.Left)
                            .ToList();
                        int insertBase = nextNonDateLefts.Count > 0
                            ? nextNonDateLefts.Min()
                            : (nextRow.Cells.Count > 0 ? nextRow.Cells.Max(c => c.Left) + 1 : 0);

                        var orphanCells = rows[i].Cells;
                        for (int j = 0; j < orphanCells.Count; j++)
                        {
                            nextRow.Cells.Add(new TableCell
                            {
                                Text = orphanCells[j].Text,
                                Left = insertBase - (orphanCells.Count - j),
                                IsContinuationDetail = orphanCells[j].IsContinuationDetail
                            });
                        }
                        continue;
                    }
                }

                result.Add(rows[i]);
            }
            return result;
        }
        private List<BankAccountSection> ExtractBteAccountSections(List<TableRow> rows, string fullText)
        {
            var sections = new List<BankAccountSection>();
            string documentRib = ExtractRib(fullText);
            string accountNumber = ExtractAccountNumber(fullText);


            var bteAmountRegex = new Regex(@"-?\d{1,3}(?:[ .,]?\d{3})*[.,]\d{1,3}");
            var dateRegex = new Regex(@"^\d{2}[/.\-]\d{2}[/.\-]\d{2,4}$");

            int? documentYear = null;
            var btePeriodeMatch = Regex.Match(fullText,
         @"P[ée]riode\s*:?\s*(?:du\s*)?\d{2}[/.\-]\d{2}[/.\-](\d{4})|AU\s+\d{2}[/.\-]\d{2}[/.\-](\d{4})",
         RegexOptions.IgnoreCase);
            if (btePeriodeMatch.Success)
            {
                var yGroup = btePeriodeMatch.Groups[1].Success ? btePeriodeMatch.Groups[1] : btePeriodeMatch.Groups[2];
                if (int.TryParse(yGroup.Value, out int py)) documentYear = py;
            }
            if (!documentYear.HasValue)
            {
                var anyDateMatch = Regex.Match(fullText, @"\d{2}[/.\-]\d{2}[/.\-](\d{4})");
                if (anyDateMatch.Success && int.TryParse(anyDateMatch.Groups[1].Value, out int ay))
                    documentYear = ay;
            }

            int? dateOpeAnchor = null, referenceAnchor = null, dateValeurAnchor = null,
                 debitAnchor = null, creditAnchor = null, soldeAnchor = null;

            foreach (var row in rows)
            {
                foreach (var cell in row.Cells.OrderBy(c => c.Left))
                {
                    string t = cell.Text.Trim();
                    if (!dateOpeAnchor.HasValue && Regex.IsMatch(t, @"^D\.?\s*Op[ée]\.?$", RegexOptions.IgnoreCase))
                        dateOpeAnchor = cell.Left;
                    else if (!referenceAnchor.HasValue && Regex.IsMatch(t, @"^R[ée]f[ée]rence$|^R[ée]f\.?$", RegexOptions.IgnoreCase))
                        referenceAnchor = cell.Left;
                    else if (!dateValeurAnchor.HasValue && Regex.IsMatch(t, @"^D\.?\s*Valeur$", RegexOptions.IgnoreCase))
                        dateValeurAnchor = cell.Left;
                    else if (!debitAnchor.HasValue && Regex.IsMatch(t, @"^D[ée]bit$", RegexOptions.IgnoreCase))
                        debitAnchor = cell.Left;
                    else if (!creditAnchor.HasValue && Regex.IsMatch(t, @"^Cr[ée]dit$", RegexOptions.IgnoreCase))
                        creditAnchor = cell.Left;
                    else if (!soldeAnchor.HasValue && Regex.IsMatch(t, @"^Solde$", RegexOptions.IgnoreCase))
                        soldeAnchor = cell.Left;
                }
                if (dateOpeAnchor.HasValue && debitAnchor.HasValue && creditAnchor.HasValue && soldeAnchor.HasValue)
                    break;
            }

            if (!debitAnchor.HasValue || !creditAnchor.HasValue)
            {
                var (inferredDebit, inferredCredit) = InferAnchorsFromAmountPositions(rows);
                if (inferredDebit.HasValue) debitAnchor = inferredDebit;
                if (inferredCredit.HasValue) creditAnchor = inferredCredit;
            }

            Console.WriteLine($"[BTE] Ancres : dateOpe={dateOpeAnchor} reference={referenceAnchor} " +
                               $"dateValeur={dateValeurAnchor} debit={debitAnchor} credit={creditAnchor} solde={soldeAnchor}");

            var current = new BankAccountSection
            {
                AccountNumber = accountNumber,
                Rib = documentRib,
                Currency = ExtractCurrency(fullText),
                SoldeInitial = null
            };

            decimal? previousSolde = null;
            string pendingDate = "";
            string pendingLibelle = "";
            string floatingLibelleBuffer = "";
            bool footerReached = false;

            bool IsHeaderOrMetadataNoise(string joined) => Regex.IsMatch(joined,
                @"D\.?\s*Op[ée]\.?\s+Libell[ée]|^Compte\s*N[°o]|^\s*RIB\b|Adresse\s*:|Intitul[ée]\s*:|" +
                @"Devise\s*:|Ville\s*:|P[ée]riode\s*:|Relev[ée]\s+de\s+Compte|Extrait\s+de\s+Compte|" +
                @"^Page\s*\d|^\d+\s*/\s*\d+\s*$",
                RegexOptions.IgnoreCase);

            bool LooksLikeFooter(string joined)
            {
                if (Regex.IsMatch(joined,
                    @"sauf\s+erreur\s+ou\s+omission|d[ée]lai\s+de\s+r[ée]clamation|jours?\s+[àa]\s+compter\s+de|" +
                    @"tacitement\s+accept[ée]|r[ée]clamation\s+(sera|doit)|tout\s+d[ée]bit\s+ponctuel|" +
                    @"dispositions?\s+l[ée]gales?|conform[ée]ment\s+aux?\s+articles?",
                    RegexOptions.IgnoreCase))
                    return true;

                if (joined.Length >= 60
                    && !dateRegex.IsMatch(joined)
                    && !bteAmountRegex.IsMatch(joined)
                    && current.Transactions.Count > 0)
                    return true;

                return false;
            }
            var lineFallbackRegex = new Regex(
                @"^(\d{2}[/.\-]\d{2}[/.\-]\d{2,4})\s+(.+?)\s+(\d{2}[/.\-]\d{2}[/.\-]\d{2,4})\s+" +
                @"(-?\d{1,3}(?:[ .,]?\d{3})*[.,]\d{1,3})" +
                @"(?:\s+(-?\d{1,3}(?:[ .,]?\d{3})*[.,]\d{1,3}))?\s*(CR|DB)?\s*$",
                RegexOptions.IgnoreCase);

            foreach (var row in rows)
            {
                var cells = row.Cells.OrderBy(c => c.Left).ToList();
                if (cells.Count == 0) continue;

                string joined = string.Join(" ", cells.Where(c => !c.IsContinuationDetail).Select(c => CleanWhitespace(c.Text)));
                string continuationText = Regex.Replace(
                    string.Join(" ", cells.Where(c => c.IsContinuationDetail).Select(c => CleanWhitespace(c.Text))),
                    @"\s{2,}", " ").Trim();
                if (string.IsNullOrWhiteSpace(joined) && string.IsNullOrWhiteSpace(continuationText)) continue;

                if (IsHeaderOrMetadataNoise(joined)) continue;
                if (!footerReached && LooksLikeFooter(joined))
                {
                    footerReached = true;
                }

                    var soldeInitMatch = Regex.Match(joined,
                    @"(SOLDE\s+PR[ée]C[ée]DENT|ANCIEN\s+SOLDE|SOLDE\s+D['’]OUVERTURE|SOLDE\s+INITIAL|SOLDE\s+AU\s*:?\s*\d{2}[/.\-]\d{2}[/.\-]\d{2,4})" +
                    @"\D*(-?\d{1,3}(?:[ .,]?\d{3})*[.,]\d{2,3})\s*(CR|DB)?",
                    RegexOptions.IgnoreCase);
                if (soldeInitMatch.Success && current.Transactions.Count == 0 && !current.SoldeInitial.HasValue)
                {
                    decimal v = ParseAmount(soldeInitMatch.Groups[2].Value);
                    bool isDb = soldeInitMatch.Groups[3].Success && soldeInitMatch.Groups[3].Value.Equals("DB", StringComparison.OrdinalIgnoreCase);
                    current.SoldeInitial = isDb ? -Math.Abs(v) : Math.Abs(v);
                    previousSolde = current.SoldeInitial;
                    continue;
                }

                var totalMatch = Regex.Match(joined,
                    @"TOTAUX?\s*:?\s*(-?\d{1,3}(?:[ .,]?\d{3})*[.,]\d{2,3})\s+(-?\d{1,3}(?:[ .,]?\d{3})*[.,]\d{2,3})",
                    RegexOptions.IgnoreCase);
                if (totalMatch.Success)
                {
                    current.TotalDebit = ParseAmount(totalMatch.Groups[1].Value);
                    current.TotalCredit = ParseAmount(totalMatch.Groups[2].Value);
                    continue;
                }

                var soldeFinalMatch = Regex.Match(joined,
                    @"(NOUVEAU\s+SOLDE|SOLDE\s+FINAL|SOLDE\s+DE\s+CLOTURE)\D*(-?\d{1,3}(?:[ .,]?\d{3})*[.,]\d{2,3})\s*(CR|DB)?",
                    RegexOptions.IgnoreCase);
                if (soldeFinalMatch.Success)
                {
                    decimal v = ParseAmount(soldeFinalMatch.Groups[2].Value);
                    bool isDb = soldeFinalMatch.Groups[3].Success && soldeFinalMatch.Groups[3].Value.Equals("DB", StringComparison.OrdinalIgnoreCase);
                    current.SoldeFinal = isDb ? -Math.Abs(v) : Math.Abs(v);
                    continue;
                }
                if (footerReached) continue;
                TableCell? dateOpeCell = cells.Where(c => !c.IsContinuationDetail).FirstOrDefault(c =>
                    (!dateOpeAnchor.HasValue || c.Left <= dateOpeAnchor.Value + 40) &&
                    Regex.IsMatch(c.Text.Trim(), @"^\d{2}[/.\-]\d{2}[/.\-]\d{2,4}$"));

                decimal? debitVal = null, creditVal = null, soldeVal = null;
                string? soldeSign = null;
                var libelleParts = new List<string>();
                if (cells.Count > 1)
                    foreach (var cell in cells)
                {
                    if (dateOpeCell != null && cell.Left == dateOpeCell.Left && cell.Text == dateOpeCell.Text)
                        continue;

                    string txt = cell.Text.Trim();
                    if (string.IsNullOrEmpty(txt)) continue;

                  
                    if (cell.IsContinuationDetail)
                        continue;

                    if (Regex.IsMatch(txt, @"^(CR|DB)$", RegexOptions.IgnoreCase))
                    {
                        soldeSign = txt.ToUpperInvariant();
                        continue;
                    }

                    bool isAmount = AmountRegex.IsMatch(txt);
                    bool isBareDigits = Regex.IsMatch(txt, @"^\d{4,10}$");
                    bool isDateValeur = Regex.IsMatch(txt, @"^\d{2}[/.\-]\d{2}[/.\-]\d{2,4}$");

                    var distances = new List<(string label, int dist)>();
                    if (referenceAnchor.HasValue) distances.Add(("reference", Math.Abs(cell.Left - referenceAnchor.Value)));
                    if (dateValeurAnchor.HasValue) distances.Add(("datevaleur", Math.Abs(cell.Left - dateValeurAnchor.Value)));
                    if (debitAnchor.HasValue) distances.Add(("debit", Math.Abs(cell.Left - debitAnchor.Value)));
                    if (creditAnchor.HasValue) distances.Add(("credit", Math.Abs(cell.Left - creditAnchor.Value)));
                    if (soldeAnchor.HasValue) distances.Add(("solde", Math.Abs(cell.Left - soldeAnchor.Value)));

                    string nearest = distances.Count > 0 ? distances.OrderBy(d => d.dist).First().label : "libelle";

                    if (isDateValeur)
                        continue;

                    if (nearest == "reference" && (isBareDigits || isAmount))
                    {
                        continue;
                    }
                    if (nearest == "debit" && isAmount)
                    {
                        debitVal = ParseAmount(AmountRegex.Match(txt).Value);
                        continue;
                    }
                    if (nearest == "credit" && isAmount)
                    {
                        creditVal = ParseAmount(AmountRegex.Match(txt).Value);
                        continue;
                    }
                    if (nearest == "solde" && isAmount)
                    {
                        soldeVal = ParseAmount(AmountRegex.Match(txt).Value);
                        continue;
                    }

                    libelleParts.Add(txt);
                }
                

                string libelleText = Regex.Replace(string.Join(" ", libelleParts), @"\s{2,}", " ").Trim();
                bool hasAnyAmount = debitVal.HasValue || creditVal.HasValue || soldeVal.HasValue;

                if (dateOpeCell == null || !hasAnyAmount)
                {
                    var fm = lineFallbackRegex.Match(joined);
                    if (fm.Success)
                    {
                        dateOpeCell = new TableCell { Text = fm.Groups[1].Value, Left = cells.First(c => !c.IsContinuationDetail).Left };
                        string middle = fm.Groups[2].Value.Trim();
                        var trailingRef = Regex.Match(middle, @"\s(\d{4,13})$");
                        if (trailingRef.Success) middle = middle.Substring(0, trailingRef.Index).Trim();
                        libelleText = middle; decimal montant = ParseAmount(fm.Groups[4].Value);
                        string? sign = fm.Groups[6].Success ? fm.Groups[6].Value.ToUpperInvariant() : null;
                        decimal montantMouv = ParseAmount(fm.Groups[4].Value);
                        if (fm.Groups[5].Success)
                        {
                            decimal soldeLigne = ParseAmount(fm.Groups[5].Value);
                            bool isDb = fm.Groups[6].Success && fm.Groups[6].Value.Equals("DB", StringComparison.OrdinalIgnoreCase);
                            decimal soldeSigne = isDb ? -Math.Abs(soldeLigne) : Math.Abs(soldeLigne);

                            if (previousSolde.HasValue)
                            {
                                decimal diff = soldeSigne - previousSolde.Value;
                                if (diff < 0) debitVal = montantMouv; else if (diff > 0) creditVal = montantMouv;
                            }
                            previousSolde = soldeSigne;
                            hasAnyAmount = debitVal.HasValue || creditVal.HasValue;
                        }
                        else
                        {
                            libelleText = (libelleText + $" [MONTANT A CLASSER: {montantMouv} - a verifier manuellement]").Trim();
                            hasAnyAmount = false;
                        }
                        if (hasAnyAmount)
                        {
                        }
                        else if (sign == "CR" || sign == "DB")
                        {
                            soldeVal = montant;
                            soldeSign = sign;
                            debitVal = null; creditVal = null;
                            hasAnyAmount = soldeVal.HasValue;
                        }
                        else if (debitAnchor.HasValue && creditAnchor.HasValue)
                        {
                            var lastCell = cells.Where(c => !c.IsContinuationDetail).LastOrDefault(c => bteAmountRegex.IsMatch(c.Text));
                            if (lastCell != null)
                            {
                                int distDebit = Math.Abs(lastCell.Left - debitAnchor.Value);
                                int distCredit = Math.Abs(lastCell.Left - creditAnchor.Value);
                                if (distDebit <= distCredit) debitVal = montant; else creditVal = montant;
                                hasAnyAmount = true;
                            }
                            else
                            {
                                libelleText = (libelleText + $" [MONTANT A CLASSER: {montant.ToString(CultureInfo.InvariantCulture)} - a verifier manuellement]").Trim();
                                hasAnyAmount = false;
                            }
                        }
                        else
                        {
                            libelleText = (libelleText + $" [MONTANT A CLASSER: {montant.ToString(CultureInfo.InvariantCulture)} - a verifier manuellement]").Trim();
                            hasAnyAmount = false;
                        }
                    }
                }

                if (!string.IsNullOrEmpty(continuationText))
                    libelleText = string.IsNullOrEmpty(libelleText) ? continuationText : (libelleText + " " + continuationText).Trim();

                decimal? SignedSolde() => soldeVal.HasValue
                    ? (string.Equals(soldeSign, "DB", StringComparison.OrdinalIgnoreCase) ? -Math.Abs(soldeVal.Value) : Math.Abs(soldeVal.Value))
                    : (decimal?)null;

                Transaction BuildTx(string date, string libelle)
                {
                    var tx = new Transaction { Date = date, Libelle = libelle };
                    if (debitVal.HasValue) tx.Debit = debitVal.Value;
                    if (creditVal.HasValue) tx.Credit = creditVal.Value;

                    var soldeSigned = SignedSolde();
                    if (!tx.Debit.HasValue && !tx.Credit.HasValue && previousSolde.HasValue && soldeSigned.HasValue)
                    {
                        decimal diff = soldeSigned.Value - previousSolde.Value;
                        if (diff < 0) tx.Debit = Math.Abs(diff);
                        else if (diff > 0) tx.Credit = diff;
                    }
                    if (soldeSigned.HasValue) previousSolde = soldeSigned;
                    return tx;
                }

                if (dateOpeCell != null)
                {
                    string date = NormalizeDate(dateOpeCell.Text.Trim(), documentYear);
                    if (string.IsNullOrEmpty(date)) continue;


                    if (!string.IsNullOrEmpty(pendingDate))
                    {
                        current.Transactions.Add(new Transaction
                        {
                            Date = pendingDate,
                            Libelle = (pendingLibelle + " [MONTANT MANQUANT - a verifier manuellement]").Trim()
                        });
                        pendingDate = ""; pendingLibelle = "";
                    }

                    string finalLibelle = string.IsNullOrEmpty(floatingLibelleBuffer)
              ? libelleText
              : (floatingLibelleBuffer + " " + libelleText).Trim();
                    floatingLibelleBuffer = "";


                    if (hasAnyAmount)
                    {
                        current.Transactions.Add(BuildTx(date, libelleText));
                    }
                    else
                    {
                        pendingDate = date;
                        pendingLibelle = libelleText;
                    }
                    continue;
                }

                if (!string.IsNullOrEmpty(pendingDate))
                {
                    if (hasAnyAmount)
                    {
                        current.Transactions.Add(BuildTx(pendingDate, (pendingLibelle + " " + libelleText).Trim()));
                        pendingDate = ""; pendingLibelle = "";
                    }
                    else if (!string.IsNullOrEmpty(libelleText))
                    {
                        pendingLibelle = (pendingLibelle + " " + libelleText).Trim();
                    }
                    continue;
                }

                if (!string.IsNullOrEmpty(libelleText) && current.Transactions.Count > 0)
                {
                    floatingLibelleBuffer = (floatingLibelleBuffer + " " + libelleText).Trim();

                }
            }

            if (!string.IsNullOrEmpty(pendingDate))
            {
                current.Transactions.Add(new Transaction
                {
                    Date = pendingDate,
                    Libelle = (pendingLibelle + " [ a verifier manuellement]").Trim()
                });
            }
            if (!string.IsNullOrEmpty(floatingLibelleBuffer) && current.Transactions.Count > 0)
            {
                var last = current.Transactions[current.Transactions.Count - 1];
                last.Libelle = (last.Libelle + " " + floatingLibelleBuffer).Trim();
            }

            current.RawSectionText = fullText;
            sections.Add(current);

            foreach (var sec in sections)
                ValidateBteAccounting(sec);

            return sections;
        }

    
        private void ValidateBteAccounting(BankAccountSection section)
        {
            if (!section.SoldeInitial.HasValue || !section.SoldeFinal.HasValue)
            {
                Console.WriteLine("[BTE] Contrôle comptable ignoré : solde initial ou final manquant.");
                return;
            }

            decimal totalDebit = section.TotalDebit ?? section.Transactions.Sum(t => t.Debit ?? 0);
            decimal totalCredit = section.TotalCredit ?? section.Transactions.Sum(t => t.Credit ?? 0);

            decimal expectedFinal = section.SoldeInitial.Value + totalCredit - totalDebit;
            decimal diff = Math.Abs(expectedFinal - section.SoldeFinal.Value);

            if (diff > 0.005m)
            {
                Console.WriteLine(
                    $"[BTE-WARN] Contrôle comptable en échec : solde_initial({section.SoldeInitial}) + " +
                    $"crédit({totalCredit}) - débit({totalDebit}) = {expectedFinal} != solde_final({section.SoldeFinal}) " +
                    $"(écart={diff})");
            }
            else
            {
                Console.WriteLine($"[BTE-OK] Contrôle comptable validé (écart={diff}).");
            }
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
                    var frDateMatch = Regex.Match(fullText,
                        @"\d{1,2}\s+[A-Za-zÀ-ÿ]{3,6}\.?\s*(\d{2,4})",
                        RegexOptions.IgnoreCase);
                    if (frDateMatch.Success && int.TryParse(frDateMatch.Groups[1].Value, out year))
                        documentYear = year < 100 ? 2000 + year : year;
                }
            }


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
            bool isAmenWithOpCode = isAmenDocument && Regex.IsMatch(fullText, @"^\s*\d{2}\s+\d{2}/\d{2}/\d{4}", RegexOptions.Multiline);
            bool isBtk = fullText.Contains("BTK", StringComparison.OrdinalIgnoreCase);


            bool isBna = fullText.Contains("BNA", StringComparison.OrdinalIgnoreCase);
            
            (int Month, int Year)? bnaReleveMonthYear = null;
            if (isBna)
            {
                var bnaMoisMatch = Regex.Match(fullText, @"DU\s+MOIS\s+DE\s*:?\s*([A-Za-zéûÉÛ]+)\s+(\d{4})", RegexOptions.IgnoreCase);
                if (bnaMoisMatch.Success)
                {
                    string moisRaw = bnaMoisMatch.Groups[1].Value;
                    foreach (var kv in FrenchMonthsAbbrev)
                    {
                        if (moisRaw.StartsWith(kv.Key, StringComparison.OrdinalIgnoreCase))
                        {
                            bnaReleveMonthYear = (int.Parse(kv.Value), int.Parse(bnaMoisMatch.Groups[2].Value));
                            break;
                        }
                    }
                }
            }
            bool isAlBarakaDoc = fullText.Contains("AlBaraka", StringComparison.OrdinalIgnoreCase)
            || fullText.Contains("albarakabank", StringComparison.OrdinalIgnoreCase);
            bool isAlBarakaExtrait = isAlBarakaDoc && Regex.IsMatch(fullText, @"Montant\s+cr[ée]diteur|Montant\s+d[ée]biteur", RegexOptions.IgnoreCase);
            bool isAlBarakaReleve = isAlBarakaDoc && !isAlBarakaExtrait;
            bool isAtb = fullText.Contains("ATB", StringComparison.OrdinalIgnoreCase)
                || fullText.Contains("Arab Tunisian Bank", StringComparison.OrdinalIgnoreCase);

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
            bool isAbcBank = fullText.Contains("Bank ABC", StringComparison.OrdinalIgnoreCase)
            || fullText.Contains("BANK ABC", StringComparison.OrdinalIgnoreCase);
            bool isAttijari = fullText.Contains("ATTIJARI", StringComparison.OrdinalIgnoreCase);

            bool hasSignedAmountsHeader = Regex.IsMatch(fullText,
                @"\(-\)\s*D[ée]bit\s*/\s*Cr[ée]dit\s*\(\+\)|\(-\)\s*D[ée]bit.*Cr[ée]dit",
                RegexOptions.IgnoreCase);

            bool hasGenuineSeparateDebitCreditHeader = HasGenuineSeparateDebitCreditHeader(rows, documentYear);
            int signedAmountSignalCount = Regex.Matches(fullText, @"-\s?\d{1,3}(?:[ .,]?\d{3})*[.,]\d{2,3}").Count;
            bool structuralSignedAmountFamily = !hasGenuineSeparateDebitCreditHeader && signedAmountSignalCount >= 5;
            if (structuralSignedAmountFamily)
                Console.WriteLine($"[FAMILLE] Montant signé détecté structurellement (pas de colonne Débit/Crédit séparée, {signedAmountSignalCount} montants signés observés).");

            bool hasSignedAmounts = isAbcBank || hasSignedAmountsHeader || isAlBarakaReleve || structuralSignedAmountFamily;

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
                    .Select(c =>
                    {
                        string t = NormalizeSignSpacing(CleanWhitespace(c.Text));
                        if (isBiat) t = ConvertFrenchAbbrevDates(t);
                        if (isAttijari) t = Regex.Replace(t, @"^\s*_\s*(?=\d)", "-");
                        return t;
                    })
                    .ToList();
                var cellTexts = MergeLoneSignCells(cellTextsRaw);

                string joined = string.Join(" ", cellTexts);
                joined = StripPrintArtifacts(joined);
                if (isBna) Console.WriteLine("[BNA-DEBUG] Cellules: " + string.Join(" || ", cellTexts.Select((c, ci) => $"[{ci}]='{c}'")));
                if (isQnb || isAlBarakaDoc || isBh || isAtb || isAbcBank || isBiat || isAttijari || isBtk) Console.WriteLine($"[DIAG-CELLS] hasSignedAmounts={hasSignedAmounts} debitAnchor={debitAnchor} creditAnchor={creditAnchor} soldeAnchor={soldeAnchor} | " + string.Join(" || ", cellTexts.Select((c, ci) => $"[{ci}]='{c}'")));
                if (string.IsNullOrWhiteSpace(joined)) continue;

                if (skippingSummaryTable)
                {
                    if (Regex.IsMatch(joined, @"Account\s*\(IBAN\)", RegexOptions.IgnoreCase))
                        skippingSummaryTable = false;
                    continue;
                }

                sectionRawText += joined + "\n";
                if (isQnb)
                {
                    Console.WriteLine($"[QNB-DEBUG] joined='{joined}' | current==null: {current == null}");

                    
                    var qnbAccountOnRow = ExtractAccountNumber(joined);
                    if (!string.IsNullOrWhiteSpace(qnbAccountOnRow)) lastSeenAccountNumber = qnbAccountOnRow;

                   
                    var qnbOpeningBalanceMatch = Regex.Match(joined,
                        @"Opening\s+Balance\s*:?\s*(-?\d{1,3}(?:[ .,]?\d{3})*[.,]\d{2,3})", RegexOptions.IgnoreCase);
                    if (qnbOpeningBalanceMatch.Success)
                    {
                        if (current == null)
                        {
                            current = new BankAccountSection
                            {
                                AccountNumber = lastSeenAccountNumber,
                                Rib = string.IsNullOrWhiteSpace(lastSeenRib) ? documentRib : lastSeenRib,
                                Currency = ExtractCurrency(fullText),
                                SoldeInitial = ParseAmount(qnbOpeningBalanceMatch.Groups[1].Value)
                            };
                            previousSolde = current.SoldeInitial;
                            sectionRawText = joined + "\n";
                        }
                        continue;
                    }

       
                    var qnbClosingBalanceMatch = Regex.Match(joined,
                        @"Closing\s+Balance\s*:?\s*_?\s*(-?\d{1,3}(?:[ .,]?\d{3})*[.,]\d{2,3})", RegexOptions.IgnoreCase);
                    if (qnbClosingBalanceMatch.Success && current != null)
                        current.SoldeFinal = ParseAmount(qnbClosingBalanceMatch.Groups[1].Value);

                   
                    bool qnbStartsWithDate = Regex.IsMatch(joined, @"^\d{2}/\d{2}/\d{4}\b");
                    if (!qnbStartsWithDate &&
                        (qnbClosingBalanceMatch.Success || Regex.IsMatch(joined, @"^Count\s*:\s*\d+\s+SUM", RegexOptions.IgnoreCase)))
                        continue;

                    if (!qnbStartsWithDate && Regex.IsMatch(joined,
                        @"Equation\s+System|TH-Account\s+Statement|Produced\s+by\s*:|Statement\s+Reference|Qatar\s+National\s+Bank\s+Page|Tran\s+Date\s+Transaction\s+Description|^IBAN\s*:|^From\s*:|^Count\s*:\s*\d+\s+SUM",
                        RegexOptions.IgnoreCase))
                        continue;

                  
                    var qnbMatch = Regex.Match(joined,
                        @"^(\d{2}/\d{2}/\d{4})\s+(.*?)\s+(-?\d{1,3}(?:[ .,]?\d{3})*[.,]\d{2,3})\s+(-?\d{1,3}(?:[ .,]?\d{3})*[.,]\d{2,3})$", RegexOptions.IgnoreCase);
                    if (!qnbMatch.Success)
                        qnbMatch = Regex.Match(joined,
                            @"^(\d{2}/\d{2}/\d{4})\s+(.*?)\s+(-?\d{1,3}(?:[ .,]?\d{3})*[.,]\d{2,3})\s+(-?\d{1,3}(?:[ .,]?\d{3})*[.,]\d{2,3})\s+(.+)$", RegexOptions.IgnoreCase);
                    if (qnbMatch.Success)
                    {
                        string date = qnbMatch.Groups[1].Value;
                        string libelle = qnbMatch.Groups[2].Value.Trim();
                        if (qnbMatch.Groups[5].Success)
                        {
                           
                            string tail = Regex.Replace(qnbMatch.Groups[5].Value.Trim(),
                                @"\bCount\s*:\s*\d+\s+SUM.*$|\bClosing\s+Balance\s*:.*$|\bQatar\s+National\s+Bank\s+Page.*$",
                                "", RegexOptions.IgnoreCase).Trim();
                            if (!string.IsNullOrEmpty(tail))
                                libelle = (libelle + " " + tail).Trim();
                        }
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
                        };

                        decimal absVal = Math.Abs(debit);
                        if (debit < 0) qnbTx.Debit = absVal;
                        else if (debit > 0) qnbTx.Credit = absVal;


                        previousSolde = solde;
                        current.Transactions.Add(qnbTx);
                        continue;
                    }
                }

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
                var account = ExtractAccountNumber(joined);
                if (!string.IsNullOrWhiteSpace(account))
                    lastSeenAccountNumber = account;

                var ribMatch = Regex.Match(joined, @"TN\d{2}[\s\d]{15,25}");
                if (ribMatch.Success) lastSeenRib = Regex.Replace(ribMatch.Value, @"\s+", "").Trim();
               
                if (Regex.IsMatch(joined, @"Type\s*d['’]op[ée]ration|Montant\s*Min|Montant\s*Max|Date\s*D[ée]but|Date\s*Fin\b|Agence\s*:|Intitul[ée]\s*de\s*compte|Solde\s*actuel\s*:", RegexOptions.IgnoreCase))
                    continue;

                if (isUbci)
                {
                    var ubciOpenMatch = Regex.Match(joined,
                        @"SOLDE\s+(DEBITEUR|CREDITEUR)\s+AU\s+\d{2}/\d{2}/\d{4}\s+(-?\d{1,3}(?:[ .,]?\d{3})*[.,]\d{2,3})",
                        RegexOptions.IgnoreCase);
                    if (current == null && ubciOpenMatch.Success)
                    {
                        decimal ubciSoldeInit = ParseAmount(ubciOpenMatch.Groups[2].Value);
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

                    var ubciCloseMatch = Regex.Match(joined, @"SOLDE\s+DE\s+CLOTURE\s+(-?\d{1,3}(?:[ .,]?\d{3})*[.,]\d{2,3})", RegexOptions.IgnoreCase);
                    if (ubciCloseMatch.Success)
                    {
                        if (current != null) current.SoldeFinal = ParseAmount(ubciCloseMatch.Groups[1].Value);
                        ubciClotureReached = true;
                        continue;
                    }
                }

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

               
                
                bool looksLikeTransactionRow = (debitCell != null || creditCell != null) &&
                    (!string.IsNullOrEmpty(GetNormalizedDateFromCells(cellTexts, documentYear)) || cells.Any(c => AmountRegex.IsMatch(c.Text)));

                if ((debitCell != null || creditCell != null) && !looksLikeTransactionRow)
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

                var soldeAuFinMatch = Regex.Match(joined, @"Solde\s*\(\w+\)\s*au\s*\d{2}/\d{2}/\d{4}\s*:?\s*(-?\d{1,3}(?:[ .,]?\d{3})*[.,]\d{2,3})", RegexOptions.IgnoreCase);
                if (soldeAuFinMatch.Success)
                {
                    if (current != null) current.SoldeFinal = ParseAmount(soldeAuFinMatch.Groups[1].Value);
                    continue;
                }
                var soldeAuGenericMatch = Regex.Match(joined,
                    @"Solde\s+au\s*:?\s*\d{2}/\d{2}/\d{4}\s+(-?\d{1,3}(?:[ .,]?\d{3})*[.,]\d{2,3})\s*(CR|DB)?",
                    RegexOptions.IgnoreCase);
                if (soldeAuGenericMatch.Success)
                {
                    decimal soldeAuVal = ParseAmount(soldeAuGenericMatch.Groups[1].Value);
                    if (soldeAuGenericMatch.Groups[2].Success)
                    {
                        soldeAuVal = soldeAuGenericMatch.Groups[2].Value.Equals("DB", StringComparison.OrdinalIgnoreCase)
                            ? -Math.Abs(soldeAuVal)
                            : Math.Abs(soldeAuVal);
                    }

                    if (current == null)
                    {
                        current = new BankAccountSection
                        {
                            AccountNumber = lastSeenAccountNumber,
                            Rib = string.IsNullOrWhiteSpace(lastSeenRib) ? documentRib : lastSeenRib,
                            Currency = ExtractCurrency(fullText),
                            SoldeInitial = soldeAuVal
                        };
                        previousSolde = soldeAuVal;
                        sectionRawText = joined + "\n";
                        pendingLibelleBuffer = "";
                        pendingDate = "";
                    }
                    else
                    {
                        current.SoldeFinal = soldeAuVal;
                    }
                    continue;
                }

                var soldeInitMatch = Regex.Match(joined, @"\bSolde\s+Initial\s*:?\s*([-+]?\d{1,3}(?:[ .,]?\d{3})*[.,]\d{2,3})", RegexOptions.IgnoreCase); if (soldeInitMatch.Success || Regex.IsMatch(joined, @"\bSolde\s+Initial\b", RegexOptions.IgnoreCase))
                {
                    if (current != null)
                    {
                        current.RawSectionText = sectionRawText;
                        sections.Add(current);
                    }
                    sectionRawText = joined + "\n";

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
                var abcSoldeDepartMatch = Regex.Match(joined,
                    @"^Solde\s+de\s+d[ée]part\s+(-?\d{1,3}(?:[ .,]?\d{3})*[.,]\d{2,3})$",
                    RegexOptions.IgnoreCase);
                if (!abcSoldeDepartMatch.Success)
                {
                    abcSoldeDepartMatch = Regex.Match(joined,
                        @"Solde\s+de\s+d[ée]part\s+(-?\d{1,3}(?:[ .,]?\d{3})*[.,]\d{2,3})",
                        RegexOptions.IgnoreCase);
                }
                if (abcSoldeDepartMatch.Success)
                {
                    if (current != null)
                    {
                        current.RawSectionText = sectionRawText;
                        sections.Add(current);
                    }
                    sectionRawText = joined + "\n";
                    decimal abcDepartInit = ParseAmount(abcSoldeDepartMatch.Groups[1].Value);
                    current = new BankAccountSection
                    {
                        AccountNumber = lastSeenAccountNumber,
                        Rib = string.IsNullOrWhiteSpace(lastSeenRib) ? documentRib : lastSeenRib,
                        Currency = ExtractCurrency(fullText),
                        SoldeInitial = abcDepartInit
                    };
                    previousSolde = abcDepartInit;
                    pendingLibelleBuffer = "";
                    pendingDate = "";
                    continue;
                }
                var biatSoldeDepartMatch = Regex.Match(joined, @"Solde\s+d[ée]part\s+au\s+\d{2}/\d{2}/\d{4}\s+(-?\d{1,3}(?:[ .,]?\d{3})*[.,]\d{2,3})", RegexOptions.IgnoreCase);
                if (biatSoldeDepartMatch.Success)
                {
                    if (current != null)
                    {
                        current.RawSectionText = sectionRawText;
                        sections.Add(current);
                    }
                    sectionRawText = joined + "\n";

                    decimal biatDepartInit = ParseAmount(biatSoldeDepartMatch.Groups[1].Value);
                    current = new BankAccountSection
                    {
                        AccountNumber = lastSeenAccountNumber,
                        Rib = string.IsNullOrWhiteSpace(lastSeenRib) ? documentRib : lastSeenRib,
                        Currency = ExtractCurrency(fullText),
                        SoldeInitial = biatDepartInit
                    };
                    previousSolde = biatDepartInit;
                    pendingLibelleBuffer = "";
                    pendingDate = "";
                    continue;
                }

                var biatSoldeFinAuMatch = Regex.Match(joined, @"Solde\s+fin\s+au\s+\d{2}/\d{2}/\d{4}\s+(-?\d{1,3}(?:[ .,]?\d{3})*[.,]\d{2,3})", RegexOptions.IgnoreCase);
                if (biatSoldeFinAuMatch.Success)
                {
                    if (current != null) current.SoldeFinal = ParseAmount(biatSoldeFinAuMatch.Groups[1].Value);
                    continue;
                }
                var biatSoldeAuMatch = Regex.Match(joined, @"SOLDE\s*AU\s*\d{1,2}\s+\d{1,2}\s+\d{4}\s+(-?\d{1,3}(?:[ .,]?\d{3})*[.,]\d{2,3})", RegexOptions.IgnoreCase);
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

                var zitounaSoldeMatch = Regex.Match(joined, @"Solde\s+actuel\s+(-?\d{1,3}(?:[ .,]?\d{3})*[.,]\d{2,3})", RegexOptions.IgnoreCase);
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

                if (Regex.IsMatch(joined, @"\bSolde\s+Final\b", RegexOptions.IgnoreCase))
                {
                    if (current != null)
                    {
                        var finalMatch = AmountRegex.Match(joined);
                        current.SoldeFinal = finalMatch.Success ? ParseAmount(finalMatch.Value) : previousSolde;
                    }
                    continue;
                }

                var biatSoldeFinalMatch = Regex.Match(joined, @"\bSOLDE\b(?!\s*AU)\s+(-?\d{1,3}(?:[ .,]?\d{3})*[.,]\d{2,3})", RegexOptions.IgnoreCase);
                if (biatSoldeFinalMatch.Success)
                {
                    if (current != null) current.SoldeFinal = ParseAmount(biatSoldeFinalMatch.Groups[1].Value);
                    continue;
                }

             
                if (current == null
                    && Regex.IsMatch(joined, @"\bSOLDE\b", RegexOptions.IgnoreCase)
                    && Regex.IsMatch(joined, @"\d{1,2}[/\-. ]\d{1,2}[/\-. ]\d{2,4}|\d{1,2}\s+[A-Za-z]{3,4}\s+\d{2,4}")
                    && Regex.IsMatch(joined, @"\b(SOLDE|BALANCE|REPORT|ANCIEN)\b", RegexOptions.IgnoreCase))
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
                var totalMouvementsMatch = Regex.Match(joined,
                    @"Total\s+des\s+mouvements\s*:?\s*(-?\d{1,3}(?:[ .,]?\d{3})*[.,]\d{2,3})\s+(-?\d{1,3}(?:[ .,]?\d{3})*[.,]\d{2,3})",
                    RegexOptions.IgnoreCase);
                if (totalMouvementsMatch.Success)
                {
                    if (current != null)
                    {
                        current.TotalDebit = ParseAmount(totalMouvementsMatch.Groups[1].Value);
                        current.TotalCredit = ParseAmount(totalMouvementsMatch.Groups[2].Value);
                    }
                    continue;
                }
                var albarakaSoldeFinMatch = Regex.Match(joined,
                    @"Solde\s+fin\s+p[ée]riode\s*:?\s*(-?\d{1,3}(?:[ .,]?\d{3})*[.,]\d{2,3})|" +
                    @"SOLDE\s+(CREDITEUR|DEBITEUR)\s*:\s*(-?\d{1,3}(?:[ .,]?\d{3})*[.,]\d{2,3})",
                    RegexOptions.IgnoreCase);
                if (albarakaSoldeFinMatch.Success)
                {
                    if (current != null)
                    {
                        if (albarakaSoldeFinMatch.Groups[1].Success)
                            current.SoldeFinal = ParseAmount(albarakaSoldeFinMatch.Groups[1].Value);
                        else
                        {
                            decimal v = ParseAmount(albarakaSoldeFinMatch.Groups[3].Value);
                            current.SoldeFinal = albarakaSoldeFinMatch.Groups[2].Value.Equals("DEBITEUR", StringComparison.OrdinalIgnoreCase)
                                ? -Math.Abs(v) : Math.Abs(v);
                        }
                    }
                    continue;
                }
                if (isBna && current == null)
                {
                    var bnaSoldeDepartMatch = Regex.Match(joined, @"SOLDE\s+DEPART\s+(-?\d{1,3}(?:[ .,]?\d{3})*[.,]\d{2,3})", RegexOptions.IgnoreCase);
                    if (bnaSoldeDepartMatch.Success)
                    {
                        current = new BankAccountSection
                        {
                            AccountNumber = lastSeenAccountNumber,
                            Rib = string.IsNullOrWhiteSpace(lastSeenRib) ? documentRib : lastSeenRib,
                            Currency = ExtractCurrency(fullText),
                            SoldeInitial = ParseAmount(bnaSoldeDepartMatch.Groups[1].Value)
                        };
                        previousSolde = current.SoldeInitial;
                        sectionRawText = joined + "\n";
                        pendingLibelleBuffer = "";
                        pendingDate = "";
                        Console.WriteLine($"[BNA-DEBUG] Ligne ignoree (SOLDE DEPART -> ouverture de section) : SoldeInitial={current.SoldeInitial}");
                        continue;
                    }
                }
              
                if (isBna)
                {
                    var bnaSoldeAuMatch = Regex.Match(joined, @"\bSOLDE\s+AU\b.*?(-?\d{1,3}(?:[ .,]?\d{3})*[.,]\d{2,3})\s*$", RegexOptions.IgnoreCase);
                    if (bnaSoldeAuMatch.Success)
                    {
                        if (current != null) current.SoldeFinal = ParseAmount(bnaSoldeAuMatch.Groups[1].Value);
                        Console.WriteLine($"[BNA-DEBUG] Ligne ignoree (SOLDE AU -> cloture de section) : SoldeFinal={current?.SoldeFinal}");
                        continue;
                    }
                    if (Regex.IsMatch(joined, @"\bTOTAUX\s+DES\s+MOUVEMENTS\b", RegexOptions.IgnoreCase))
                    {
                        Console.WriteLine("[BNA-DEBUG] Ligne ignoree (TOTAUX DES MOUVEMENTS -> recapitulatif, pas une operation)");
                        continue;
                    }
                }
                if (Regex.IsMatch(joined, @"\b(Totaux?|Page\s*\d)\b", RegexOptions.IgnoreCase))
                    continue;

                if (isAmenDocument && current != null && current.Transactions.Count > 0
                    && !joined.Contains('/') && Regex.IsMatch(joined.Trim(), @"^\d{2}\.\d{2}\.\d{4}\s*\S*$"))
                {
                    var lastTx = current.Transactions[current.Transactions.Count - 1];
                    lastTx.Libelle = (lastTx.Libelle + " " + joined.Trim()).Trim();
                    continue;
                }

                if (isQnb && Regex.IsMatch(joined, @"\b(Pour toute remarque|N\.B:|support@|hotline|Page\s*\d+ of \d+)\b", RegexOptions.IgnoreCase))
                    continue;

               
                if (isBtk && Regex.IsMatch(joined, @"Titulaire\s+du\s+compte|Valeur\s+Libell[ée]|Report\s+du\s+\d{2}[/\-.]\d{2}[/\-.]\d{2,4}|Page\s+sur\s+\d+", RegexOptions.IgnoreCase))
                    continue;

                string normalizedDate = GetNormalizedDateFromCells(cellTexts, documentYear);
                if (string.IsNullOrEmpty(normalizedDate) && isAmenWithOpCode && cellTexts.Count >= 2)
                {
                    if (Regex.IsMatch(cellTexts[0].Trim(), @"^\d{2}$"))
                    {
                        normalizedDate = NormalizeDate(cellTexts[1].Trim(), documentYear);
                    }
                }
                if (isBna && string.IsNullOrEmpty(normalizedDate))
                {
                    if (!soldeAnchor.HasValue)
                    {
                       
                        if (bnaReleveMonthYear.HasValue
                            && Regex.IsMatch(cellTexts.Count > 0 ? cellTexts[0].Trim() : "", @"^\d{1,2}$")
                            && int.TryParse(cellTexts[0].Trim(), out int bnaJour) && bnaJour >= 1 && bnaJour <= 31)
                        {
                            normalizedDate = $"{bnaJour:D2}/{bnaReleveMonthYear.Value.Month:D2}/{bnaReleveMonthYear.Value.Year}";
                        }
                    }
                    else if (AmountRegex.Matches(joined).Count >= 2)
                    {
                       
                        normalizedDate = GetDateFromAnyCellLenient(cellTexts, documentYear);
                    }
                }
                
                if (isAtb && string.IsNullOrEmpty(normalizedDate))
                {
                    normalizedDate = GetDateFromAnyCell(cellTexts, null);
                }
               
                if (!isBna && string.IsNullOrEmpty(normalizedDate))
                {
                    normalizedDate = GetDateFromAnyCell(cellTexts, documentYear);
                }

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

                if (isBna)
                    Console.WriteLine($"[BNA-DEBUG] Date detectee='{(string.IsNullOrEmpty(normalizedDate) ? "(aucune)" : normalizedDate)}' | pendingDate='{pendingDate}' | joined='{joined}'");

                var mergedWithPos = new List<(int Left, string Text, bool IsContinuationDetail)>();
                for (int i = 0; i < cells.Count; i++)
                {
                    string cellRawText = isAmenDocument ? StripAmenEchoDate(cells[i].Text) : cells[i].Text;
                    if (isBna) cellRawText = StripBnaRateLabel(cellRawText);
                    if (isAttijari) cellRawText = Regex.Replace(cellRawText, @"^\s*_\s*(?=\d)", "-");
                    mergedWithPos.Add((cells[i].Left, NormalizeSignSpacing(CleanWhitespace(cellRawText)), cells[i].IsContinuationDetail));
                }
                for (int i = 0; i < mergedWithPos.Count - 1; i++)
                {
                    if (mergedWithPos[i].Text.Trim() == "-")
                    {
                        mergedWithPos[i + 1] = (mergedWithPos[i + 1].Left, "-" + mergedWithPos[i + 1].Text.TrimStart(), mergedWithPos[i + 1].IsContinuationDetail);
                        mergedWithPos.RemoveAt(i);
                        i--;
                    }
                }

                var amountCandidates = mergedWithPos
                    .Where(c => !c.IsContinuationDetail && AmountRegex.IsMatch(c.Text))
                    .Select(c => new { Left = c.Left, Value = ParseAmount(AmountRegex.Match(c.Text).Value) })
                    .ToList();

                
               amountCandidates = amountCandidates
                    .GroupBy(c => new { c.Value, ZoneLeft = c.Left / 20 })
                    .Select(g => g.First())
                    .ToList();
                if (debitAnchor.HasValue || creditAnchor.HasValue || soldeAnchor.HasValue)
                {
                    
                    bool shortAmountsAllowed = ((isUbci && !ubciClotureReached) || isBna) && Regex.IsMatch(joined, @"[A-Za-zÀ-ÿ]");
                    string bareDigitPattern = shortAmountsAllowed ? @"^-?\d{3,10}$" : @"^-?\d{5,10}$";
                    var bareDigitCandidates = mergedWithPos
                        .Where(c => !c.IsContinuationDetail)
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
                if (isAlBarakaDoc)
                    Console.WriteLine($"[ALBARAKA-DEBUG] joined='{joined}' | date='{normalizedDate}' | pendingDate='{pendingDate}' | amountCandidates=[{string.Join(", ", ((IEnumerable<dynamic>)amountCandidates).Select(a => $"{a.Left}:{a.Value}"))}]");
                if (isBtk)
                    Console.WriteLine($"[BTK-DEBUG] joined='{joined}' | date='{normalizedDate}' | pendingDate='{pendingDate}' | amountCandidates=[{string.Join(", ", ((IEnumerable<dynamic>)amountCandidates).Select(a => $"{a.Left}:{a.Value}"))}]");

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

                bool biatNoiseHit = isBiat && IsBiatNoise(joined);
                if (biatNoiseHit) biatInNoiseZone = true;

               
                string repeatedNoiseKey = Regex.Replace(joined, @"\d", "#").Trim();
                bool isRepeatedBoilerplate = amountCandidates.Count == 0
                    && joined.Length >= 40
                    && repeatedLineCounts.GetValueOrDefault(repeatedNoiseKey) >= 3;

                bool bnaNoiseHit = isBna && Regex.IsMatch(joined, @"ce\s*jour\s*sauf\s*erreur\s*ou\s*omission|\bBNA\s+H24\b|TOUTE\s+ERREUR\s+OU\s+OMISSION|EST\s+A\s+SIGNALER", RegexOptions.IgnoreCase);
                if (bnaNoiseHit) biatInNoiseZone = true;

                bool albarakaNoiseHit = isAlBarakaDoc && Regex.IsMatch(joined,
                    @"Cet\s+extrait\s+est\s+consid[ée]r[ée]|Compliments\s+www\.albarakabank|La\s+banque\s+pratique", RegexOptions.IgnoreCase);
                if (albarakaNoiseHit) biatInNoiseZone = true;

                bool isAccountHeaderNoise = LibelleHeaderFooterNoiseRegex.IsMatch(joined);

                bool isNoise = Regex.IsMatch(joined, @"\b(Totaux?|Page\s*\d|Solde\s*(Initial|Final)|[ée]v[èe]nements?|\(\*\)|Solde\s*\(\w+\)\s*au|BTK@?DIRECT|https?://\S+|Report|A\s+[Rr]eporter)", RegexOptions.IgnoreCase)
                     || biatNoiseHit
                     || bnaNoiseHit
                     || albarakaNoiseHit
                     || isRepeatedBoilerplate
                     || isAccountHeaderNoise
                     || (isQnb && Regex.IsMatch(joined, @"Cette\s+d[ée]claration\s+sera\s+consid[ée]r[ée]e|dans\s+votre\s+situation\s+de\s+compte|Pour\s+toute\s+r[ée]clamation", RegexOptions.IgnoreCase));
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
                            if (!isBiat && current.Transactions.Count > 0 && !isNoise)

                                pendingDate = current.Transactions[current.Transactions.Count - 1].Date;
                            else
                                continue;
                        }

                        string fullDesc = pendingLibelleBuffer.Trim();

                       
                        if (isAmenDocument || isBtk || isUbci || isBna)
                        {
                            string currentNonAmount = string.Join(" ", cellTexts.Where(c => !AmountRegex.IsMatch(isAmenDocument ? StripAmenEchoDate(c) : c))).Trim();
                            if (isAmenDocument) currentNonAmount = StripAmenEchoDate(currentNonAmount);
                            if (isBna && !soldeAnchor.HasValue) currentNonAmount = StripBnaFooterBoilerplate(StripBnaReleveValeurDate(currentNonAmount));
                            currentNonAmount = Regex.Replace(currentNonAmount, @"\b\d{2}[/\-.]\d{2}[/\-.]\d{4}\b", "").Trim();
                            currentNonAmount = Regex.Replace(currentNonAmount, @"\b\d{8}\b", "").Trim();
                            if (!string.IsNullOrEmpty(currentNonAmount))
                                fullDesc = (fullDesc + " " + currentNonAmount).Trim();
                        }
                        pendingLibelleBuffer = "";

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
                        current.Transactions.Add(tx2);
                        if (isBna) Console.WriteLine($"[BNA-DEBUG] Transaction creee (ligne sans date propre) : Date='{tx2.Date}' Libelle='{tx2.Libelle}' Debit={tx2.Debit} Credit={tx2.Credit}");
                        pendingDate = "";
                        biatInNoiseZone = false;
                        continue;
                    }

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
                        string textOnly = string.Join(" ", cellTexts.Skip(1).Where(c =>
                            !AmountRegex.IsMatch(isAmenDocument ? StripAmenEchoDate(c) : c)
                            && !LibelleHeaderFooterNoiseRegex.IsMatch(c)));
                        if (isAmenDocument) textOnly = StripAmenEchoDate(textOnly);
                        pendingLibelleBuffer = (pendingLibelleBuffer + " " + textOnly).Trim();
                    }
                    continue;
                }


                if (current != null)
            {
                    int skipCount = (isAmenWithOpCode && cellTexts.Count >= 2
                        && Regex.IsMatch(cellTexts[0].Trim(), @"^\d{2}$")) ? 2 : 1;
                    string description = string.Join(" ", cellTexts.Skip(1).Where(c =>
                        (isAttijari ? !Regex.IsMatch(c.Trim(), @"^-?\d{1,3}(?:[ .,]?\d{3})*[.,]\d{1,3}$") : !AmountRegex.IsMatch(isAmenDocument ? StripAmenEchoDate(c) : c))
                        && !LibelleHeaderFooterNoiseRegex.IsMatch(c)))
                    .Trim(' ', '|', '[', ']', '-', '_');
                if (isAmenDocument) description = StripAmenEchoDate(description);
                if (isBna && !soldeAnchor.HasValue) description = StripBnaFooterBoilerplate(StripBnaReleveValeurDate(description));
                if (isAlBarakaDoc) description = StripAlBarakaFooterBoilerplate(description);
                description = Regex.Replace(description, @"\b\d{2}[/\-.]\d{2}[/\-.]\d{4}\b", "").Trim();
                description = Regex.Replace(description, @"\b\d{8}\b", "").Trim();
                description = Regex.Replace(description, @"\s{2,}", " ").Trim();

                if (isQnb && Regex.IsMatch(description, @"support|hotline|N\.B|Pour toute|Cette déclaration", RegexOptions.IgnoreCase))
                    continue;

                string fullDescription = (pendingLibelleBuffer + " " + description).Trim();
                pendingLibelleBuffer = "";
                pendingDate = "";


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
                current.Transactions.Add(tx);
                if (isBna) Console.WriteLine($"[BNA-DEBUG] Transaction creee : Date='{tx.Date}' Libelle='{tx.Libelle}' Debit={tx.Debit} Credit={tx.Credit}");
                biatInNoiseZone = false;
            }
            }


            if (current != null && !sections.Contains(current))
            {
                current.RawSectionText = sectionRawText;
                sections.Add(current);
            }
            foreach (var sec in sections)
            {
                if (sec.Transactions.Count == 0)
                {
                    TryFallbackLineParsing(fullText, sec, documentYear);
                }
            }

            foreach (var sec in sections)
            {
                var totalDebitMatch = Regex.Match(sec.RawSectionText,
                    @"Total\s*(?:des\s*)?D[ée]bit(?:s)?\s*:?\s*(-?\d{1,3}(?:[ .,]?\d{3})*[.,]\d{2,3})", RegexOptions.IgnoreCase);
                if (totalDebitMatch.Success) sec.TotalDebit = ParseAmount(totalDebitMatch.Groups[1].Value);

                var totalCreditMatch = Regex.Match(sec.RawSectionText,
                    @"Total\s*(?:des\s*)?Cr[ée]dit(?:s)?\s*:?\s*(-?\d{1,3}(?:[ .,]?\d{3})*[.,]\d{2,3})", RegexOptions.IgnoreCase);
                if (totalCreditMatch.Success) sec.TotalCredit = ParseAmount(totalCreditMatch.Groups[1].Value);

                if (!sec.TotalDebit.HasValue && !sec.TotalCredit.HasValue)
                {
                    var totalTwoNumbers = Regex.Match(sec.RawSectionText,
                        @"Total\s+(-?\d{1,3}(?:[ .,]?\d{3})*[.,]\d{2,3})\s+(-?\d{1,3}(?:[ .,]?\d{3})*[.,]\d{2,3})", RegexOptions.IgnoreCase);
                    if (totalTwoNumbers.Success)
                    {
                        sec.TotalDebit = ParseAmount(totalTwoNumbers.Groups[1].Value);
                        sec.TotalCredit = ParseAmount(totalTwoNumbers.Groups[2].Value);
                    }
                }
            }

            return sections;
        }
        private bool TryParseUbciFormatB(string fullText, BankAccountSection section, int? documentYear)
        {

            var lines = fullText.Split('\n')
                .Select(l => l.Trim())
                .Where(l => !string.IsNullOrWhiteSpace(l))
                .ToList();

            var txLineRegex = new Regex(
                @"^(\d{2}/\d{2}/\d{4})\s+(.+?)\s+\d{2}/\d{2}/\d{4}\s+\S+\s*$",
                RegexOptions.IgnoreCase);

            var amountOnlyRegex = new Regex(
                @"^-?\d{1,3}(?:[.,\s]\d{3})*[.,]\d{2,3}$");

            int found = 0;
            for (int i = 0; i < lines.Count - 1; i++)
            {
                if (txLineRegex.IsMatch(lines[i]) && amountOnlyRegex.IsMatch(lines[i + 1]))
                {
                    found++;
                    if (found >= 3) return true;
                }
            }
            return false;
        }

        private List<BankAccountSection> ParseUbciFormatB(string fullText,
            string documentRib, string accountNumber, int? documentYear,
            DateTime? periodStart, DateTime? periodEnd)
        {
            var sections = new List<BankAccountSection>();
            var current = new BankAccountSection
            {
                AccountNumber = accountNumber,
                Rib = documentRib,
                Currency = "TND",
                SoldeInitial = null
            };

            var soldeInitMatch = Regex.Match(fullText,
                @"SOLDE\s+DEBITEUR\s+AU\s+\d{2}/\d{2}/\d{4}\s+(-?\d{1,3}(?:[.,\s]\d{3})*[.,]\d{2,3})",
                RegexOptions.IgnoreCase);
            if (!soldeInitMatch.Success)
                soldeInitMatch = Regex.Match(fullText,
                    @"SOLDE\s+CREDITEUR\s+AU\s+\d{2}/\d{2}/\d{4}\s+(-?\d{1,3}(?:[.,\s]\d{3})*[.,]\d{2,3})",
                    RegexOptions.IgnoreCase);
            if (soldeInitMatch.Success)
                current.SoldeInitial = ParseAmount(soldeInitMatch.Groups[1].Value);

            var soldeFinalMatch = Regex.Match(fullText,
                @"SOLDE\s+DE\s+CLOTURE\s+(-?\d{1,3}(?:[.,\s]\d{3})*[.,]\d{2,3})",
                RegexOptions.IgnoreCase);
            if (soldeFinalMatch.Success)
                current.SoldeFinal = ParseAmount(soldeFinalMatch.Groups[1].Value);

            var totalMatch = Regex.Match(fullText,
                @"TOTAL\s+DU\s+DEBIT\s+ET\s+DU\s+CREDIT\s+(\d{1,3}(?:[.,\s]\d{3})*[.,]\d{2,3})\s+(\d{1,3}(?:[.,\s]\d{3})*[.,]\d{2,3})",
                RegexOptions.IgnoreCase);
            if (totalMatch.Success)
            {
                current.TotalDebit = ParseAmount(totalMatch.Groups[1].Value);
                current.TotalCredit = ParseAmount(totalMatch.Groups[2].Value);
            }

            var lines = fullText.Split('\n')
                .Select(l => l.Trim())
                .ToList();


            var txInlineRegex = new Regex(
                @"^(\d{2}/\d{2}/\d{4})\s+(.+?)\s+(-?\d{1,3}(?:[.,]\d{3})*[.,]\d{2,3})\s+\d{2}/\d{2}/\d{4}\s+\S+\s*$",
                RegexOptions.IgnoreCase);

            var txNoAmountRegex = new Regex(
                @"^(\d{2}/\d{2}/\d{4})\s+(.+?)\s+\d{2}/\d{2}/\d{4}\s+\S+\s*$",
                RegexOptions.IgnoreCase);

            var amountOnlyRegex = new Regex(
                @"^(-?\d{1,3}(?:[.,\s]\d{3})*[.,]\d{2,3})$");

            var noiseRegex = new Regex(
                @"TOTAL\s+DU\s+DEBIT|SOLDE\s+DE\s+CLOTURE|SOLDE\s+DEBITEUR|SOLDE\s+CREDITEUR|" +
                @"Page\s+\d+|Natures\s+des|Date\s+op|Pour\s+plus|Vous\s+êtes|L'UBCI|" +
                @"PERIODE\s+DU|Rapport\s+d|www\.ubci|bensalah|SWIFT|R\.N\.E|MONTPLAISIR|" +
                @"BOUMHEL|Agence|AVENUE|RIB\s*:|CIF\s+CLIENT|RELEVE\s+DE|EXTRAIT\s+DE|" +
                @"كشف|الشرف|الرصيد|نقدم|Nous\s+avons|presente,\s+sauf|Société\s+Anonyme",
                RegexOptions.IgnoreCase);

            var headerRegex = new Regex(
                @"^(Date\s+op|Natures\s+des|Débit|Crédit|Date\s+valeur|Ref\s+Banque)",
                RegexOptions.IgnoreCase);

            for (int i = 0; i < lines.Count; i++)
            {
                string line = lines[i];
                if (string.IsNullOrWhiteSpace(line)) continue;
                if (noiseRegex.IsMatch(line)) continue;
                if (headerRegex.IsMatch(line)) continue;

                var m1 = txInlineRegex.Match(line);
                if (m1.Success)
                {
                    string date = m1.Groups[1].Value;
                    string libelle = CleanUbciLibelle(m1.Groups[2].Value);
                    decimal montant = ParseAmount(m1.Groups[3].Value);

                    if (!IsUbciLibelleNoise(libelle))
                    {
                        var tx = BuildUbciTransaction(date, libelle, montant, documentYear);
                        if (tx != null) current.Transactions.Add(tx);
                    }
                    continue;
                }

                var m2 = txNoAmountRegex.Match(line);
                if (m2.Success)
                {
                    string date = m2.Groups[1].Value;
                    string libelle = CleanUbciLibelle(m2.Groups[2].Value);

                    decimal? montant = null;
                    for (int j = i + 1; j < Math.Min(i + 4, lines.Count); j++)
                    {
                        string nextLine = lines[j].Trim();
                        if (string.IsNullOrWhiteSpace(nextLine)) continue;
                        var am = amountOnlyRegex.Match(nextLine);
                        if (am.Success)
                        {
                            montant = ParseAmount(am.Groups[1].Value);
                            i = j;
                            break;
                        }
                        if (Regex.IsMatch(nextLine, @"^\d{2}/\d{2}/\d{4}\s+")) break;
                    }

                    if (!IsUbciLibelleNoise(libelle))
                    {
                        if (montant.HasValue)
                        {
                            var tx = BuildUbciTransaction(date, libelle, montant.Value, documentYear);
                            if (tx != null) current.Transactions.Add(tx);
                        }
                        else
                        {
                            current.Transactions.Add(new Transaction
                            {
                                Date = NormalizeDate(date, documentYear),
                                Libelle = libelle + " [MONTANT MANQUANT - a verifier manuellement]"
                            });
                        }
                    }
                    continue;
                }
            }

            current.RawSectionText = fullText;
            sections.Add(current);
            return sections;
        }

        private string CleanUbciLibelle(string raw)
        {
            string result = raw.Trim();
            result = Regex.Replace(result, @"\s+\d{2}/\d{2}/\d{4}\s+\S+\s*$", "").Trim();
            result = Regex.Replace(result, @"\s+[0-9A-Za-z]{10,30}\s*$", "").Trim();
            result = Regex.Replace(result, @"\s{2,}", " ").Trim();
            return result;
        }

        private bool IsUbciLibelleNoise(string libelle)
        {
            return string.IsNullOrWhiteSpace(libelle)
                || libelle.Length < 3
                || Regex.IsMatch(libelle, @"^[\-_\|\.]+$")
                || Regex.IsMatch(libelle, @"^[a-z]?\.\s*\""?\s*", RegexOptions.IgnoreCase)
                || Regex.IsMatch(libelle,
                    @"l\.\s*""\s*ate_|Date_|opération|Natures\s+des|Débit|Crédit|Ref\s+Banque",
                    RegexOptions.IgnoreCase);
        }

        private Transaction? BuildUbciTransaction(string date, string libelle, decimal montant, int? documentYear)
        {
            string normalizedDate = NormalizeDate(date, documentYear);
            if (string.IsNullOrEmpty(normalizedDate)) return null;

            var tx = new Transaction
            {
                Date = normalizedDate,
                Libelle = libelle
            };

            if (montant < 0)
            {
                tx.Debit = Math.Abs(montant);
            }
            else if (montant > 0)
            {
                bool isDebit = IsUbciDebitByLibelle(libelle);
                if (isDebit) tx.Debit = montant;
                else tx.Credit = montant;
            }

            return tx;
        }

        private bool IsUbciDebitByLibelle(string libelle)
        {
            return Regex.IsMatch(libelle,
                @"COMMISSION|PRELEVEMENT\s+RECU|TVA|PDL|FRAIS|PAIEMENT|CHEQUE\s+RECU\s+TELECOMPENSATION|" +
                @"VIREMENT\s+DOMESTIQUE\s+EMIS|VIREMENT\s+PERMANENT\s+EMIS|PAYM\s+CARTE|LOYER|" +
                @"ABONNEMENT|TARIFICATION|EFFET\s+RECU",
                RegexOptions.IgnoreCase);
        }
        private void TryFallbackLineParsing(string fullText, BankAccountSection section, int? documentYear)
        {

            Console.WriteLine($"[FALLBACK] Tentative sur texte de {fullText.Length} chars");
            Console.WriteLine($"[FALLBACK] Premiers 500 chars : {fullText.Substring(0, Math.Min(500, fullText.Length))}");

            var lines = fullText.Split('\n');
            Console.WriteLine($"[FALLBACK] Nombre de lignes : {lines.Length}");
            foreach (var line in lines.Take(20))
            {
                Console.WriteLine($"[FALLBACK-LINE] '{line}'");
            }
            var lineRegex = new Regex(
                @"(?:^|\s)(\d{2}/\d{2}/\d{4})\s+(.+?)\s+(\d{2}/\d{2}/\d{4})\s+(-?\d{1,3}(?:[ .,]?\d{3})*[.,]\d{2,3})\s*$",
                RegexOptions.Multiline);

            var amenRegex = new Regex(
                @"^\s*\d{2,3}\s+(\d{2}/\d{2}/\d{4})\s+(.+?)\s+(\d{2}/\d{2}/\d{4})\s+(-?\d{1,3}(?:[ .,]?\d{3})*[.,]\d{2,3})\s*$",
                RegexOptions.Multiline);

            var simpleRegex = new Regex(
                @"^\s*(\d{2}/\d{2}/\d{4})\s+(.+?)\s+(-?\d{1,3}(?:[ .,]?\d{3})*[.,]\d{2,3})\s*$",
                RegexOptions.Multiline);

            bool found = false;

            foreach (Match m in amenRegex.Matches(fullText))
            {
                string dateOp = m.Groups[1].Value;
                string libelle = m.Groups[2].Value.Trim();
                decimal montant = ParseAmount(m.Groups[4].Value);

                if (Regex.IsMatch(libelle, @"Totaux|Report|TOTAUX|Solde|Total|Page", RegexOptions.IgnoreCase))
                    continue;

                var tx = new Transaction
                {
                    Date = NormalizeDate(dateOp, documentYear),
                    Libelle = libelle
                };

                if (montant < 0) tx.Debit = Math.Abs(montant);
                else if (montant > 0) tx.Credit = montant;

                if (!string.IsNullOrEmpty(tx.Date))
                {
                    section.Transactions.Add(tx);
                    found = true;
                }
            }

            if (found) return;

            foreach (Match m in lineRegex.Matches(fullText))
            {
                string dateOp = m.Groups[1].Value;
                string libelle = m.Groups[2].Value.Trim();
                decimal montant = ParseAmount(m.Groups[4].Value);

                if (Regex.IsMatch(libelle, @"Totaux|Report|TOTAUX|Solde|Total|Page", RegexOptions.IgnoreCase))
                    continue;

                var tx = new Transaction
                {
                    Date = NormalizeDate(dateOp, documentYear),
                    Libelle = libelle
                };

                if (montant < 0) tx.Debit = Math.Abs(montant);
                else if (montant > 0) tx.Credit = montant;

                if (!string.IsNullOrEmpty(tx.Date))
                {
                    section.Transactions.Add(tx);
                    found = true;
                }
            }

            if (found) return;

            foreach (Match m in simpleRegex.Matches(fullText))
            {
                string dateOp = m.Groups[1].Value;
                string libelle = m.Groups[2].Value.Trim();
                decimal montant = ParseAmount(m.Groups[3].Value);

                if (Regex.IsMatch(libelle, @"Totaux|Report|TOTAUX|Solde|Total|Page", RegexOptions.IgnoreCase))
                    continue;

                var tx = new Transaction
                {
                    Date = NormalizeDate(dateOp, documentYear),
                    Libelle = libelle
                };

                if (montant < 0) tx.Debit = Math.Abs(montant);
                else if (montant > 0) tx.Credit = montant;

                if (!string.IsNullOrEmpty(tx.Date))
                {
                    section.Transactions.Add(tx);
                }
            }
        }
        private static readonly string[] BhKnownLibellePrefixes = {
              "COMFORC PRLV.", "VERSEMENT TPE", "VERS.CHQ.ORDIN", "VRST.AUT.AG.",
            "ENC.CHQ.TN", "ENC.EFFET TN", "Vers ESP RECU",
            "COMMISSION", "T.V.A", "TV.A", "TVA", "PRLV.", "VRST.",

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



        
        private bool HasGenuineSeparateDebitCreditHeader(List<TableRow> rows, int? documentYear)
        {
            foreach (var row in rows)
            {
                var cells = row.Cells.OrderBy(c => c.Left).ToList();
                bool hasDebitWord = cells.Any(c => Regex.IsMatch(c.Text, @"D[ée]bit", RegexOptions.IgnoreCase));
                bool hasCreditWord = cells.Any(c => Regex.IsMatch(c.Text, @"Cr[ée]dit", RegexOptions.IgnoreCase));
                if (!hasDebitWord && !hasCreditWord) continue;

                var cellTexts = cells.Select(c => NormalizeSignSpacing(CleanWhitespace(c.Text))).ToList();
                bool looksLikeTransaction = !string.IsNullOrEmpty(GetNormalizedDateFromCells(cellTexts, documentYear))
                    || cells.Any(c => AmountRegex.IsMatch(c.Text));
                if (!looksLikeTransaction) return true;
            }
            return false;
        }

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

            const string BhDatePattern = @"\d{4}-\d{1,2}-\d{1,2}|\d{1,2}[/\-.]\d{1,2}(?:[/\-.]\d{2,4})?";

            int? documentYear = null;
            var bhFullYearMatch = Regex.Match(fullText, @"\b\d{1,2}[/\-.]\d{1,2}[/\-.](\d{4})\b");
            if (bhFullYearMatch.Success)
            {
                documentYear = int.Parse(bhFullYearMatch.Groups[1].Value);
            }
            else
            {
                var bhShortYearMatch = Regex.Match(fullText, @"\b\d{1,2}-\d{1,2}-(\d{2})\b");
                if (bhShortYearMatch.Success)
                    documentYear = 2000 + int.Parse(bhShortYearMatch.Groups[1].Value);
            }

            var bhLineRegex = new Regex(
            $@"^(?:\d+\s+)?({BhDatePattern})\s+(.+?)\s+(?:({BhDatePattern})\s+)?(-?\d[\d\s.,]*\d|\d)\s*$",
            RegexOptions.IgnoreCase);

            var soldeOuvertureRegex = new Regex(@"^(-?\d[\d\s.,]*\d|\d)\s*$");
            var soldeAuLabelRegex = new Regex(@"Solde\s+au\s+\d{2}/\d{2}/\d{4}", RegexOptions.IgnoreCase);
            var soldeAuInlineRegex = new Regex(
                @"SOLDE\s+AU\s+\d{1,2}[/\-.]\d{1,2}[/\-.]\d{2,4}\s+(-)?\s*(\d{1,3}(?:[ .,]?\d{3})*[.,]\d{2,3})",
                RegexOptions.IgnoreCase);
            var bhClotureRegex = new Regex(
                @"\b(DEBITEUR|CREDITEUR)\s+(-?\d{1,3}(?:[ .,]?\d{3})*[.,]\d{2,3})",
                RegexOptions.IgnoreCase);
            var soldeAuLabelOnlyRegex = new Regex(
                @"SOLDE\s+AU\s+\d{1,2}[/\-.]\d{1,2}[/\-.]\d{2,4}\s*(-)?\s*$", RegexOptions.IgnoreCase);
            var bareAmountAnywhereRegex = new Regex(@"(\d{1,3}(?:[ .,]?\d{3})*[.,]\d{2,3})");

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

                int NearbyNonBlankLineIndex(int startIndex, int step, int maxHops)
                {
                    int idx = startIndex;
                    for (int hop = 0; hop < maxHops; hop++)
                    {
                        idx += step;
                        if (idx < 0 || idx >= lines.Length) return -1;
                        if (!string.IsNullOrWhiteSpace(lines[idx])) return idx;
                    }
                    return -1;
                }

                void AssignBhSoldeAu(int lineIndex, decimal soldeAuVal)
                {
                    bool precededByTotaux = false;
                    int idx = lineIndex;
                    for (int back = 0; back < 3; back++)
                    {
                        idx = NearbyNonBlankLineIndex(idx, -1, 20);
                        if (idx < 0) break;
                        if (Regex.IsMatch(lines[idx].Trim(), @"^\W{0,3}Tota(?:l|ux)\b", RegexOptions.IgnoreCase))
                        {
                            precededByTotaux = true;
                            break;
                        }
                    }
                    if (precededByTotaux)
                        current.SoldeFinal = soldeAuVal;
                    else if (!current.SoldeInitial.HasValue)
                        current.SoldeInitial = soldeAuVal;
                }

                var soldeAuInlineMatch = soldeAuInlineRegex.Match(line);
                if (soldeAuInlineMatch.Success)
                {
                    decimal soldeAuVal = ParseAmount(soldeAuInlineMatch.Groups[2].Value);
                    if (soldeAuInlineMatch.Groups[1].Success) soldeAuVal = -soldeAuVal;
                    AssignBhSoldeAu(i, soldeAuVal);
                    continue;
                }

                var soldeAuLabelOnlyMatch = soldeAuLabelOnlyRegex.Match(line);
                if (soldeAuLabelOnlyMatch.Success)
                {
                    int nextIdx = NearbyNonBlankLineIndex(i, 1, 20);
                    string nextSoldeLine = nextIdx >= 0 ? lines[nextIdx].Trim() : "";
                    var nextAmountMatch = bareAmountAnywhereRegex.Match(nextSoldeLine);
                    if (nextAmountMatch.Success)
                    {
                        decimal soldeAuVal = ParseAmount(nextAmountMatch.Groups[1].Value);
                        if (soldeAuLabelOnlyMatch.Groups[1].Success) soldeAuVal = -soldeAuVal;
                        AssignBhSoldeAu(i, soldeAuVal);
                    }
                    continue;
                }

                if (soldeAuLabelRegex.IsMatch(line) && !Regex.IsMatch(line, @"^\d{2}/\d{2}/\d{4}"))
                    continue;

                var bhClotureMatch = bhClotureRegex.Match(line);
                if (bhClotureMatch.Success)
                {
                    decimal clotureVal = ParseAmount(bhClotureMatch.Groups[2].Value);
                    current.SoldeFinal = bhClotureMatch.Groups[1].Value.Equals("DEBITEUR", StringComparison.OrdinalIgnoreCase)
                        ? -Math.Abs(clotureVal) : Math.Abs(clotureVal);
                    continue;
                }

                if (Regex.IsMatch(line, @"^-?\d[\d\s.,]*\s+-?\d[\d\s.,]*$"))
                    continue;

                if (Regex.IsMatch(line, @"^\W{0,3}Tota(?:l|ux)\b", RegexOptions.IgnoreCase))
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
                    Date = NormalizeDate(dateOp, documentYear),
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

        private string GetDateFromAnyCellLenient(List<string> cellTexts, int? defaultYear)
        {
            foreach (var cell in cellTexts)
            {
                var m = Regex.Match(cell, @"\d{1,2}[/\-.]\d{1,2}[/\-.]\d{2,4}");
                if (!m.Success) continue;
                string normalized = NormalizeDate(m.Value, defaultYear);
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
                @"Num[ée]ro\s*de\s*Compte\s*:?\s*(\d[\d\s-]{6,25})",
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

           
            string accountNumber = "";
            var ubciAccountMatch = Regex.Match(fullText, @"\b(\d{15,25})\s*TND\b", RegexOptions.IgnoreCase);
            if (ubciAccountMatch.Success)
                accountNumber = ubciAccountMatch.Groups[1].Value;

            var seenRowKeys = new HashSet<string>();
            var dedupedRows = new List<TableRow>();
            foreach (var row in rows)
            {
                string rowKey = string.Join("|", row.Cells.Select(c => CleanWhitespace(c.Text).Trim()));
                if (string.IsNullOrWhiteSpace(rowKey)) continue;
                if (!seenRowKeys.Add(rowKey)) continue;
                dedupedRows.Add(row);
            }

            DateTime? ubciPeriodStart = null, ubciPeriodEnd = null;
            var ubciPeriodeMatch = Regex.Match(fullText,
                @"PERIODE\s+DU\s+(\d{2}/\d{2}/\d{4})\s+AU\s+(\d{2}/\d{2}/\d{4})",
                RegexOptions.IgnoreCase);
            if (ubciPeriodeMatch.Success)
            {
                if (DateTime.TryParseExact(ubciPeriodeMatch.Groups[1].Value, "dd/MM/yyyy",
                    CultureInfo.InvariantCulture, DateTimeStyles.None, out var ps)) ubciPeriodStart = ps;
                if (DateTime.TryParseExact(ubciPeriodeMatch.Groups[2].Value, "dd/MM/yyyy",
                    CultureInfo.InvariantCulture, DateTimeStyles.None, out var pe)) ubciPeriodEnd = pe;
            }
            int? documentYear = ubciPeriodStart?.Year ?? ubciPeriodEnd?.Year;

            int? debitAnchor = null, creditAnchor = null, dateValeurAnchor = null;
            foreach (var row in dedupedRows)
            {
                var cells = row.Cells.OrderBy(c => c.Left).ToList();
                var dCell = cells.FirstOrDefault(c =>
                    Regex.IsMatch(c.Text.Trim(), @"^D[ée]bit$", RegexOptions.IgnoreCase));
                var cCell = cells.FirstOrDefault(c =>
                    Regex.IsMatch(c.Text.Trim(), @"^Cr[ée]dit$", RegexOptions.IgnoreCase));
                var dvCell = cells.FirstOrDefault(c =>
                    Regex.IsMatch(c.Text.Trim(), @"Date\s*valeur", RegexOptions.IgnoreCase));
                if (dCell != null) debitAnchor = dCell.Left;
                if (cCell != null) creditAnchor = cCell.Left;
                if (dvCell != null) dateValeurAnchor = dvCell.Left;
                if (debitAnchor.HasValue && creditAnchor.HasValue) break;
            }

            if (!debitAnchor.HasValue || !creditAnchor.HasValue)
            {
                var amountPositions = new List<int>();
                foreach (var row in dedupedRows)
                    foreach (var cell in row.Cells)
                        if (AmountRegex.IsMatch(cell.Text.Trim()))
                            amountPositions.Add(cell.Left);

                if (amountPositions.Count >= 4)
                {
                    var sorted = amountPositions.Distinct().OrderBy(x => x).ToList();
                    int bestGap = 0, bestIdx = 0;
                    for (int i = 0; i < sorted.Count - 1; i++)
                    {
                        int gap = sorted[i + 1] - sorted[i];
                        if (gap > bestGap) { bestGap = gap; bestIdx = i; }
                    }
                    if (bestGap > 10)
                    {
                        int leftCluster = (int)sorted.Take(bestIdx + 1).Average();
                        int rightCluster = (int)sorted.Skip(bestIdx + 1).Average();
                        if (!debitAnchor.HasValue) debitAnchor = leftCluster;
                        if (!creditAnchor.HasValue) creditAnchor = rightCluster;
                        Console.WriteLine($"[UBCI] Ancres inférées : Débit~{debitAnchor} Crédit~{creditAnchor}");
                    }
                }
            }

            var dateCellPositions = new List<int>();
            foreach (var row in dedupedRows)
                foreach (var cell in row.Cells)
                    if (Regex.IsMatch(cell.Text.Trim(), @"^\d{2}/\d{2}/\d{4}$"))
                        dateCellPositions.Add(cell.Left);

            int dateOpAnchor = dateCellPositions.Count > 0 ? dateCellPositions.Min() : 0;
            int dateOpMaxLeft = dateOpAnchor + 30;

            Console.WriteLine($"[UBCI] dateOpAnchor={dateOpAnchor} dateOpMaxLeft={dateOpMaxLeft} " +
                              $"debitAnchor={debitAnchor} creditAnchor={creditAnchor} dateValeurAnchor={dateValeurAnchor}");

            var current = new BankAccountSection
            {
                AccountNumber = accountNumber,
                Rib = documentRib,
                Currency = "TND",
                SoldeInitial = null
            };

            string pendingDate = "";
            string pendingLibelle = "";
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
                else candidates.Add(rawDigits.Remove(5, 1).Remove(2, 1));

                foreach (var candidate in candidates)
                {
                    if (candidate.Length != 8) continue;
                    if (!DateTime.TryParseExact(candidate, "ddMMyyyy",
                        CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)) continue;
                    if (ubciPeriodStart.HasValue && parsed < ubciPeriodStart.Value.AddDays(-5)) continue;
                    if (ubciPeriodEnd.HasValue && parsed > ubciPeriodEnd.Value.AddDays(5)) continue;
                    if (ubciLastConfirmedDate.HasValue &&
                        Math.Abs((parsed - ubciLastConfirmedDate.Value).TotalDays) > 31) continue;
                    repaired = parsed.ToString("dd/MM/yyyy");
                    return true;
                }
                return false;
            }

            string JoinedRowText(TableRow r) =>
                string.Join(" ", r.Cells.OrderBy(c => c.Left).Select(c => CleanWhitespace(c.Text).Trim()));
            var consumedRowIndices = new HashSet<int>();

            for (int rowIdx = 0; rowIdx < dedupedRows.Count; rowIdx++)
            {
                if (consumedRowIndices.Contains(rowIdx)) continue;
                var row = dedupedRows[rowIdx];
                var cells = row.Cells.OrderBy(c => c.Left).ToList();
                if (cells.Count == 0) continue;

                string joined = string.Join(" ", cells.Select(c => CleanWhitespace(c.Text).Trim()));
                if (string.IsNullOrWhiteSpace(joined)) continue;

                var openMatch = Regex.Match(joined,
                    @"SOLDE\s+(DEBITEUR|CREDITEUR)\s+AU\s+(?:\d{2}/\d{2}/\d{4}\s+)?(-?\d{1,3}(?:[.,]\d{3})*[.,]\d{2,3})",
                    RegexOptions.IgnoreCase);
                if (openMatch.Success)
                {
                    decimal init = ParseAmount(openMatch.Groups[2].Value);
                    current.SoldeInitial = openMatch.Groups[1].Value
                        .Equals("DEBITEUR", StringComparison.OrdinalIgnoreCase)
                        ? -Math.Abs(init) : Math.Abs(init);
                    continue;
                }

                if (Regex.IsMatch(joined, @"^D[ée]bit$|^Cr[ée]dit$|Date\s*valeur|Natures\s+des|^Date\s*$|^opération$",
                    RegexOptions.IgnoreCase)) continue;

                if (Regex.IsMatch(joined, @"TOTAL\s+DU\s+DEBIT\s+ET\s+DU\s+CREDIT", RegexOptions.IgnoreCase))
                {
                    var amounts = AmountRegex.Matches(joined);
                    if (amounts.Count >= 2)
                    {
                        current.TotalDebit = ParseAmount(amounts[0].Value);
                        current.TotalCredit = ParseAmount(amounts[1].Value);
                    }
                    else if (rowIdx + 1 < dedupedRows.Count)
                    {
                        var nextAmounts = AmountRegex.Matches(JoinedRowText(dedupedRows[rowIdx + 1]));
                        if (nextAmounts.Count >= 2)
                        {
                            current.TotalDebit = ParseAmount(nextAmounts[0].Value);
                            current.TotalCredit = ParseAmount(nextAmounts[1].Value);
                            consumedRowIndices.Add(rowIdx + 1);
                        }
                    }
                    ubciClotureReached = true;
                    continue;
                }
                if (Regex.IsMatch(joined, @"SOLDE\s+DE\s+CLOTURE", RegexOptions.IgnoreCase))
                {
                    var m = AmountRegex.Match(joined);
                    if (m.Success)
                    {
                        current.SoldeFinal = ParseAmount(m.Value);
                    }
                    else if (rowIdx + 1 < dedupedRows.Count)
                    {
                        string nextJoined = JoinedRowText(dedupedRows[rowIdx + 1]).Trim();
                        var closingMatch = Regex.Match(nextJoined, @"^-?\d{1,3}(?:[ .,]?\d{3})*[.,]\d{2,3}\b");
                        if (closingMatch.Success)
                        {
                            current.SoldeFinal = ParseAmount(closingMatch.Value);
                            consumedRowIndices.Add(rowIdx + 1);
                        }
                    }
                    ubciClotureReached = true;
                    continue;
                }
                if (ubciClotureReached) continue;

                if (Regex.IsMatch(joined,
                    @"Cette\s+op[ée]ration\s+est\s+provisoire|^R\.?\s*N\.?\s*E\b|SWIFT|" +
                    @"Pour\s+plus\s+de\s+d[ée]tails|Vous\s+[êe]tes\s+tenu|L'UBCI\s+s'engage|" +
                    @"bensalah|www\.ubci|Page\s*\d+\s*/|Soci[eé]t[eé]\s+Anonyme|" +
                    @"capital\s+de\b|Si[eè]ge\s+Social|Identifiant\s+Unique|" +
                    @"Tunis[\s\-]?Cedex|Tunis[\s\-]?Belv[eé]d[eè]re|Avenue\s+de\s+la\s+Libert",
                    RegexOptions.IgnoreCase)) continue;


                int montantDebutX = debitAnchor.HasValue
                    ? Math.Min(debitAnchor.Value, creditAnchor ?? debitAnchor.Value) - 60
                    : int.MaxValue;
                int montantFinX = dateValeurAnchor.HasValue
                    ? dateValeurAnchor.Value + 15
                    : int.MaxValue;
                int tolerance = 50;
                TableCell? dateCellOp = cells.FirstOrDefault(c =>
                    c.Left <= dateOpMaxLeft &&
                    Regex.IsMatch(c.Text.Trim(), @"^\d{2}/\d{2}/\d{4}$"));

                if (dateCellOp == null)
                {
                    var fusedCell = cells.FirstOrDefault(c =>
                        c.Left <= dateOpMaxLeft &&
                        Regex.IsMatch(c.Text.Trim(), @"^\d{9,10}$"));
                    if (fusedCell != null && TryRepairUbciDate(fusedCell.Text.Trim(), out var repaired))
                    {
                        dateCellOp = new TableCell { Text = repaired, Left = fusedCell.Left };
                    }
                }

                var libCells = cells
                    .Where(c => c.Left > dateOpMaxLeft && c.Left < montantDebutX)
                    .Select(c => CleanWhitespace(c.Text).Trim())
                    .Where(t => !string.IsNullOrWhiteSpace(t)
                             && !Regex.IsMatch(t, @"^\d{2}/\d{2}/\d{4}$")
                             && !Regex.IsMatch(t, @"^[A-Z0-9]{12,30}$"))
                    .ToList();
                string libelleFromCells = Regex.Replace(string.Join(" ", libCells), @"\s{2,}", " ").Trim();

                var montantCells = cells
      .Where(c => c.Left >= montantDebutX && c.Left < montantFinX
               && AmountRegex.IsMatch(c.Text.Trim())
               && !Regex.IsMatch(c.Text.Trim(), @"^\d{2}[/.\-]\d{2}[/.\-]\d{2,4}$"))
      .ToList();

                var bareDigitMontantCells = cells
                    .Where(c => c.Left >= montantDebutX && c.Left < montantFinX
                             && !AmountRegex.IsMatch(c.Text.Trim())
                             && Regex.IsMatch(c.Text.Trim(), @"^-?\d{3,7}$"))
                    .Select(c =>
                    {
                        string digits = c.Text.Trim();
                        bool neg = digits.StartsWith("-");
                        if (neg) digits = digits.Substring(1);
                        string intPart = digits.Length > 3 ? digits.Substring(0, digits.Length - 3) : "0";
                        string decPart = digits.Substring(digits.Length - 3);
                        string repairedText = (neg ? "-" : "") + intPart + "," + decPart;
                        return new TableCell { Text = repairedText, Left = c.Left };
                    })
                    .ToList();
                montantCells.AddRange(bareDigitMontantCells);
                montantCells = montantCells.OrderBy(c => c.Left).ToList();

                montantCells = montantCells
                    .Where(c => ParseAmount(AmountRegex.Match(c.Text).Value) != 100007645.000m)
                    .ToList();

                Console.WriteLine($"[UBCI-ROW] dateOp={dateCellOp?.Text ?? "NULL"} " +
                                  $"libelle='{libelleFromCells}' " +
                                  $"montants={montantCells.Count} " +
                                  $"({string.Join(",", montantCells.Select(m => $"x={m.Left}:{m.Text}"))})");

                if (dateCellOp != null && montantCells.Count > 0)
                {
                    if (!string.IsNullOrEmpty(pendingDate))
                    {
                        current.Transactions.Add(new Transaction
                        {
                            Date = pendingDate,
                            Libelle = (pendingLibelle + " [MONTANT MANQUANT - a verifier manuellement]").Trim()
                        });
                        pendingDate = ""; pendingLibelle = "";
                    }

                    string date = NormalizeDate(dateCellOp.Text.Trim(), documentYear);
                    if (string.IsNullOrEmpty(date)) continue;

                    string fullLibelle = string.IsNullOrEmpty(pendingLibelle)
                        ? libelleFromCells
                        : (pendingLibelle + " " + libelleFromCells).Trim();
                    pendingDate = ""; pendingLibelle = "";

                    if (string.IsNullOrWhiteSpace(fullLibelle))
                        fullLibelle = "[LIBELLE MANQUANT - a verifier manuellement]";

                    foreach (var mc in montantCells)
                    {
                        decimal montant = ParseAmount(AmountRegex.Match(mc.Text).Value);
                        var tx = new Transaction { Date = date, Libelle = fullLibelle };
                        UbciAssignDebitCredit(tx, montant, mc.Left, debitAnchor, creditAnchor, tolerance);
                        if (!string.IsNullOrEmpty(tx.Date)) current.Transactions.Add(tx);
                    }

                    if (DateTime.TryParseExact(date, "dd/MM/yyyy",
                        CultureInfo.InvariantCulture, DateTimeStyles.None, out var cd))
                        ubciLastConfirmedDate = cd;
                    continue;
                }

                if (dateCellOp != null && montantCells.Count == 0)
                {
                    if (!string.IsNullOrEmpty(pendingDate))
                    {
                        current.Transactions.Add(new Transaction
                        {
                            Date = pendingDate,
                            Libelle = (pendingLibelle + " [MONTANT MANQUANT - a verifier manuellement]").Trim()
                        });
                    }
                    pendingDate = NormalizeDate(dateCellOp.Text.Trim(), documentYear);
                    pendingLibelle = libelleFromCells;
                    continue;
                }

                if (dateCellOp == null && montantCells.Count > 0 && !string.IsNullOrEmpty(pendingDate))
                {
                    string extraLib = libelleFromCells;
                    string fullLibelle = string.IsNullOrEmpty(extraLib)
                        ? pendingLibelle
                        : (pendingLibelle + " " + extraLib).Trim();

                    foreach (var mc in montantCells)
                    {
                        decimal montant = ParseAmount(AmountRegex.Match(mc.Text).Value);
                        var tx = new Transaction { Date = pendingDate, Libelle = fullLibelle };
                        UbciAssignDebitCredit(tx, montant, mc.Left, debitAnchor, creditAnchor, tolerance);
                        current.Transactions.Add(tx);
                    }

                    if (DateTime.TryParseExact(pendingDate, "dd/MM/yyyy",
                        CultureInfo.InvariantCulture, DateTimeStyles.None, out var cd))
                        ubciLastConfirmedDate = cd;

                    pendingDate = ""; pendingLibelle = "";
                    continue;
                }

                if (dateCellOp == null && montantCells.Count > 0 && string.IsNullOrEmpty(pendingDate)
                    && ubciLastConfirmedDate.HasValue
                    && libelleFromCells.Length <= 60)
                {
                    string orphanDate = ubciLastConfirmedDate.Value.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture);
                    string orphanLibelle = (string.IsNullOrWhiteSpace(libelleFromCells) ? "" : libelleFromCells + " ")
                        + "[MONTANT ORPHELIN - a verifier manuellement]";

                    foreach (var mc in montantCells)
                    {
                        decimal montant = ParseAmount(AmountRegex.Match(mc.Text).Value);
                        var tx = new Transaction { Date = orphanDate, Libelle = orphanLibelle };
                        UbciAssignDebitCredit(tx, montant, mc.Left, debitAnchor, creditAnchor, tolerance);
                        current.Transactions.Add(tx);
                    }
                    continue;
                }

                if (dateCellOp == null && montantCells.Count == 0
                    && !string.IsNullOrEmpty(pendingDate)
                    && !string.IsNullOrEmpty(libelleFromCells))
                {
                    pendingLibelle = (pendingLibelle + " " + libelleFromCells).Trim();
                }
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

        private void UbciAssignDebitCredit(Transaction tx, decimal montant, int cellLeft,
            int? debitAnchor, int? creditAnchor, int tolerance)
        {
            if (debitAnchor.HasValue && creditAnchor.HasValue)
            {
                int distDebit = Math.Abs(cellLeft - debitAnchor.Value);
                int distCredit = Math.Abs(cellLeft - creditAnchor.Value);
                if (distDebit <= distCredit) tx.Debit = Math.Abs(montant);
                else tx.Credit = Math.Abs(montant);
            }
            else
            {
                UbciAssignByLibelle(tx, montant, tx.Libelle);
            }
        }

        private void UbciAssignByLibelle(Transaction tx, decimal montant, string libelle)
        {
            bool isDebit = Regex.IsMatch(libelle,
                @"^\s*(COMMISSION|TVA\b|PDL\b|FRAIS|PAIEMENT|PAYM\s+CARTE|LOYER|ABONNEMENT|" +
                @"TARIFICATION|EFFET\s+RECU|PRELEVEMENT\s+RECU|CHEQUE\s+RECU\s+TELECOMPENSATION|" +
                @"VIREMENT\s+DOMESTIQUE\s+EMIS|VIREMENT\s+PERMANENT\s+EMIS|REGLEMENT\s+CHEQUE)",
                RegexOptions.IgnoreCase);
            if (isDebit) tx.Debit = Math.Abs(montant);
            else tx.Credit = Math.Abs(montant);
        }
        private void AssignAmounts(Transaction tx, dynamic amountCandidates, int? debitAnchor, int? creditAnchor, int? soldeAnchor, int? montantAnchor, bool isBtk, ref decimal? previousSolde , bool hasSignedAmounts, out decimal? soldeCourant)
        {
            const int Tolerance = 15;

            soldeCourant = null;

            if (debitAnchor.HasValue && creditAnchor.HasValue)
            {
                var ambiguous = new List<dynamic>();

              
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

                if (!isBtk && !soldeCourant.HasValue && soldeAnchor.HasValue && amountCandidates.Count > 1)
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
                    if (hasSignedAmounts)
                    {
                        if (val < 0) tx.Debit = Math.Abs(val);
                        else if (val > 0) tx.Credit = val;
                    }
                    else
                    {
                        if (val < 0) tx.Debit = Math.Abs(val);
                        else if (val > 0) tx.Credit = val;
                    }
                    soldeCourant = null;
                }
                else
                {
                    decimal mouvement = amounts[0];
                    decimal solde = amounts[amounts.Count - 1];
                    soldeCourant = solde;
                    if (hasSignedAmounts)
                    {
                        if (mouvement < 0) tx.Debit = Math.Abs(mouvement);
                        else if (mouvement > 0) tx.Credit = mouvement;
                        soldeCourant = null;
                    }
                    else if (previousSolde.HasValue)
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

     
        private static bool IsBiatNoise(string text)
        {
            if (ArabicScriptRegex.IsMatch(text)) return true;
            return Regex.IsMatch(text,
                
                @"Titulaire\s+du\s+compte|N[°o]?\s*de\s+compte|\bRIB\b|Cher\s+client|Agence\s*:\s*Mr\b|Fonds\s+de\s+Garantie|RELEVE\s+.*MENSUEL|BANQUE\s+INTERNATIONALE\s+ARABE|TOTAUX|Nous\s+avons\s+l.honneur|Nous\s+vous\s+prions\s+de\s+contacter",
                RegexOptions.IgnoreCase);
        }

       
        private static bool IsBiatArabicHeaderCell(string text, string keyword)
        {
            string trimmed = text.Trim().Trim('‏', '‎', '.', ':', '»', '«', '،', ' ');
            return trimmed.Length > 0 && trimmed.Length <= 8 && trimmed.Contains(keyword);
        }

     
        private static readonly string[] BhDebitKeywords =
        {
            "PRLV.", "COMMISSION", "T.V.A", "TV.A", "TVA", "COMFORC", "VIR.TN MM BQ"
        };

        private bool IsBhDebitLibelle(string libelle)
        {

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
        private static readonly Dictionary<string, string> FrenchMonthsAbbrev = new(StringComparer.OrdinalIgnoreCase)
        {
            {"janv","01"}, {"fevr","02"}, {"févr","02"}, {"mars","03"}, {"avr","04"},
            {"mai","05"}, {"juin","06"}, {"juil","07"}, {"aout","08"}, {"août","08"},
            {"sept","09"}, {"oct","10"}, {"nov","11"}, {"dec","12"}, {"déc","12"},
        };

        private string ConvertFrenchAbbrevDates(string text)
        {
            return Regex.Replace(text, @"(\d{1,2})\s*([A-Za-zéûÉÛ]{3,5})\.?\s*(\d{2,4})", m =>
            {
                string day = m.Groups[1].Value.PadLeft(2, '0');
                string monthRaw = m.Groups[2].Value;
                string year = m.Groups[3].Value;
                foreach (var kv in FrenchMonthsAbbrev)
                    if (monthRaw.StartsWith(kv.Key, StringComparison.OrdinalIgnoreCase))
                        return $"{day}/{kv.Value}/{(year.Length == 2 ? "20" + year : year)}";
                return m.Value;
            });
        }
        private string ExtractBankName(string text)
        {
            if (text.Contains("Banque de Tunisie et des Emirats", StringComparison.OrdinalIgnoreCase)
                || Regex.IsMatch(text, @"\bBTE\b"))
            {
                return "Banque de Tunisie et des Emirats (BTE)";
            }
            if (text.Contains("AlBaraka", StringComparison.OrdinalIgnoreCase))
            {
                return "Al Baraka Bank Tunisia";
            }
            var knownBanks = new (string Keyword, string FullName)[]
            {
                ("BNA", "Banque Nationale Agricole (BNA)"),
                ("BIAT", "Banque Internationale Arabe de Tunisie (BIAT)"),
                ("STB", "Société Tunisienne de Banque (STB)"),
                ("ATB", "Arab Tunisian Bank (ATB)"),
                ("UIB", "Union Internationale de Banques (UIB)"),
                ("ATTIJARI", "Attijari Bank"),
                ("AMEN BANK", "Amen Bank"),
                ("WIFAK", "Wifak Bank"),
                ("BH", "Banque de l'Habitat (BH)"),
                ("BTK", "Banque Tuniso-Koweitienne (BTK)") ,
                ("ALBARAKA", "Al Baraka Bank Tunisia"),
                ("QNB", "Qatar National Bank (QNB)"),
                ("Bank ABC", "Bank ABC Tunisia"),
                ("BTE", "Banque Tuniso-Emiratie (BTE)"),
                ("BANK ABC", "Bank ABC Tunisia")
            };

            foreach (var bank in knownBanks)
                if (text.Contains(bank.Keyword))
                    return bank.FullName;

            return "";
        }
    }
}