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
            // Un releve ATB peut mentionner "UBCI" en interne (ex. "Encaissement CHQ-UBCI ...",
            // un cheque tire sur un compte UBCI encaisse par ce client ATB) sans etre lui-meme un
            // releve UBCI - "ATB" apparait alors, lui, comme identifiant de banque explicite
            // (en-tete/SWIFT). Le nom complet de la banque ("Union Bancaire...") reste, lui,
            // un signal non ambigu meme si "ATB" est aussi present, et n'est donc pas concerne
            // par cette exclusion.
            bool isUbci = (fullText.Contains("UBCI", StringComparison.OrdinalIgnoreCase) && !isAtb)
                || Regex.IsMatch(fullText, @"UNION\s+BANCAIRE\s+POUR\s+LE\s+COMMERCE\s+ET\s+L['’]?\s*INDUSTRIE", RegexOptions.IgnoreCase);

            // Banque Tuniso-Libyenne : releves "Extrait de compte" dont l'entete ("No du compte
            // -", "... du compte : ...") declenche a tort les heuristiques isBh ci-dessous.
            bool isBtl = fullText.Contains("TUNISO-LIBYENNE", StringComparison.OrdinalIgnoreCase)
                || Regex.IsMatch(fullText, @"\bBTL\b", RegexOptions.IgnoreCase);

            // Tunisian Saudi Bank : entete "D. Opé. Libellé Référence D. Valeur Débit Crédit
            // Solde" + "Extrait de compte" declenche a tort bteStructuralFallback ci-dessous.
            // Regex tolerante a l'OCR sur "Tunisian" (vu lu "Tunision" sur EXTRAIT TSB.pdf).
            // "Ma banque et plus" (slogan TSB) : signal de secours pour un sous-format "Relevé de
            // Compte" (ex. releve_compte (1).pdf) qui ne reimprime nulle part le nom de banque.
            bool isTsb = Regex.IsMatch(fullText, @"Tunisi\w*\s+Saudi\s+Bank", RegexOptions.IgnoreCase)
                || Regex.IsMatch(fullText, @"\bTSB\b", RegexOptions.IgnoreCase)
                || fullText.Contains("Ma banque et plus", StringComparison.OrdinalIgnoreCase);

            // Un releve ATB peut mentionner "BTE" en interne (ex. "Encaissement CHQ-BTE ...", un
            // cheque tire sur un compte BTE encaisse par ce client ATB) sans etre lui-meme un
            // releve BTE - meme classe de faux positif que l'exclusion BTK ci-dessus. Egalement
            // vu sur un releve ATTIJARI dont une transaction referencait "BTE" comme tiers (meme
            // defaut que la collision TSB/ATTIJARI documentee plus bas pour isTsbDoc) : sans cette
            // exclusion, TOUT le document basculait a tort sur le parsing BTE, donnant 0
            // transaction reconnue.
            bool bteNameHit = (Regex.IsMatch(fullText, @"\bBTE\b", RegexOptions.IgnoreCase)
                    && !fullText.Contains("BTK", StringComparison.OrdinalIgnoreCase)
                    && !isAtb
                    && !fullText.Contains("ATTIJARI", StringComparison.OrdinalIgnoreCase))
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
                && !isBiat && !isZitouna && !isBtk && !isBna && !isWifak && !isAtb && !isUbci && !isAbcBankDoc && !isTsb
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

            bool isBh = !isBtl && (
                fullText.Contains("bhbank", StringComparison.OrdinalIgnoreCase)
                || fullText.Contains("BH BANK", StringComparison.OrdinalIgnoreCase)
                || fullText.Contains("Banque de l'Habitat", StringComparison.OrdinalIgnoreCase)
                || bankName.Contains("Habitat", StringComparison.OrdinalIgnoreCase)
                || bankName.Contains("(BH)", StringComparison.OrdinalIgnoreCase)
                || bhSignalCount >= 2);

            if (isBh)
            {
                // Reconstruction en table (positions X/Y des mots OCR) plutot que le texte brut
                // ligne par ligne : sur certains releves BH (ex. "FAH DISTRIBUTION"), Tesseract lit
                // la colonne Debit/Credit comme un bloc separe, loin des lignes Date/Libelle
                // correspondantes, dans l'ordre de lecture brut - le texte plat perd alors tout
                // lien entre une transaction et son montant (0 transaction extraite). BuildTable
                // regroupe par position verticale reelle sur la page et reattache correctement
                // chaque montant a sa ligne, independamment de l'ordre de lecture OCR.
                var bhRows = engine.BuildTable(lines);
                var bhDocument = new BankDocument
                {
                    BankName = "Banque de l'Habitat (BH)",
                    Accounts = ExtractBhAccountSections(fullText, bhRows)
                };
                return bhDocument;
            }
            if ((isBiat || isZitouna || isBtk || isBna || isBh || isWifak || isUbci || isBte || isAttijari) && !isAbcBankDoc) engine.VerticalTolerance = 1;

            var rows = engine.BuildTable(lines);
            rows = SplitDuplicatedRows(rows);
            if (isAttijari) rows = ReattachAttijariOrphanLabel(rows);

            // BTL : dates abrégées ("19 JAN 24") jamais reconnues par ClassifyDateAnywhereRegex
            // (format numérique DD/MM/YYYY uniquement) - utilisé par MergeContinuationLines pour
            // décider si une ligne est une nouvelle transaction. Sans cette conversion prealable,
            // une ligne dont le montant est en plus mal lu par l'OCR (ex. "9179499" sans séparateur
            // décimal) perd ses deux seuls signaux (date ET montant) et se retrouve fusionnée dans
            // le libellé de la transaction précédente au lieu d'être une transaction a part entière
            // - observé sur bTLextrait (2).pdf. Conversion en dates numériques ici (avant la fusion
            // de lignes) pour que ClassifyDateAnywhereRegex la reconnaisse, sans toucher au format
            // affiché ensuite (NormalizeDate gère aussi bien "19 JAN 24" que "19/01/2024").
            if (isBtl)
            {
                foreach (var r in rows)
                    foreach (var c in r.Cells)
                        c.Text = ConvertFrenchAbbrevDates(c.Text);
            }

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

            // ExtractBankName() recherche "ATTIJARI" sensible a la casse (necessaire par
            // ailleurs pour eviter qu'UIB matche a tort dans "Bourguiba") et rate donc les
            // documents ou l'OCR rend le nom en casse mixte ("Attijari bank" au lieu de
            // "ATTIJARI"), laissant bankName vide. isAttijari (ligne ~108, insensible a la
            // casse) detecte deja correctement ces documents et pilote deja leur traitement de
            // tableau : on reutilise ce meme signal existant comme filet de secours pour
            // BankName plutot que de dupliquer une detection, sans toucher a ExtractBankName.
            if (string.IsNullOrEmpty(bankName) && isAttijari) bankName = "Attijari Bank";

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
            @"^\d{2}[/.\-]\d{2}[/.\-]\d{2,4}\s+\d{2}[/.\-]\d{2}[/.\-]\d{2,4}\s*-?\s*(Date\s+de)?\s*$|" +
            // Bruit de bas/haut de page d'un export web (ex. BTK@DIRECT) : URL et compteur de
            // page "X/Y" reimprimes sur chaque page, jamais du texte de transaction pour aucune
            // banque - ajout purement additif, ne retire que ce type de bruit.
            @"https?://\S+|^\s*\d{1,3}\s*/\s*\d{1,3}\s*$|" +
            // Bandeau ATTIJARI reimprime sur chaque page (logo/en-tete + pied de page legal
            // bilingue) : sans ces motifs, ce texte se retrouvait colle au libelle de la
            // transaction voisine (avant/apres un saut de page) au lieu d'etre ignore comme le
            // bruit repetitif qu'il est. "I\.?B\.?A\.?N\.?" en plus de "IBAN\b" car imprime avec
            // des points ("I.B.A.N.") qui cassent la limite de mot du \b existant.
            @"Identit[ée]\s+Bancaire|I\.?B\.?A\.?N\.?|attijaribank\.com|R\.C\.B\s+\d{4,6}\s+\d{4}|" +
            @"SA\s+au\s+capital|Site\s+Web\s*:|Attijari\s+bank\b|AGENCE\s+LE\s+KRAM|" +
            @"Pensez\s+[àa]\s+informer\s+votre\s+agence|Mieux\s+vous\s+(connaitre|connaître|servir)",
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
                // "Solde" seul apparaît aussi dans des phrases hors-tableau plus haut dans le
                // document (ex. "Solde au 28/02/2026 ... DB"), bien avant l'en-tete reel du
                // tableau -- si on l'acceptait la, l'ancre Solde se figeait sur cette phrase et
                // cassait ensuite toute la classification Débit/Crédit/Solde des lignes de
                // transaction (le vrai montant pris pour un solde, le vrai solde pris pour un
                // credit). On ne l'accepte donc que sur la ligne qui contient AUSSI l'en-tete
                // Débit ou Crédit -- la vraie ligne d'en-tete de colonnes.
                bool rowHasDebitOrCreditHeader = row.Cells.Any(c =>
                    Regex.IsMatch(c.Text.Trim(), @"^D[ée]bit$|^Cr[ée]dit$", RegexOptions.IgnoreCase));

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
                    else if (!soldeAnchor.HasValue && rowHasDebitOrCreditHeader && Regex.IsMatch(t, @"^Solde$", RegexOptions.IgnoreCase))
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

                // "Total des mouvements" (ex. EXTR BTE 02 2026.pdf) est une autre formulation du
                // meme recapitulatif que "TOTAUX" (deja gere ci-dessous, ex. RELEVEE BTE 06-2026.pdf)
                // - ajoutee en alternative sans retirer "TOTAUX?" pour ne rien changer aux fichiers
                // qui l'utilisent deja.
                var totalMatch = Regex.Match(joined,
                    @"(?:TOTAUX?|Total\s+des\s+mouvements)\s*:?\s*(-?\d{1,3}(?:[ .,]?\d{3})*[.,]\d{2,3})\s+(-?\d{1,3}(?:[ .,]?\d{3})*[.,]\d{2,3})",
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

                // Ce releve n'a en realite qu'UNE seule colonne de montant (Débit et Crédit ne
                // sont pas deux colonnes visuellement separees malgre l'en-tete) : le sens reel
                // (debit/credit) est donne par le marqueur textuel "DB"/"CR" en fin de ligne
                // (deja capture ci-dessus dans soldeSign), pas par la position geometrique du
                // chiffre -- qui reste la meme que l'operation soit un debit ou un credit. On
                // utilise donc ce marqueur, quand present, pour corriger la classification faite
                // par proximite ci-dessus.
                if (soldeSign == "CR" && debitVal.HasValue && !creditVal.HasValue)
                {
                    creditVal = debitVal;
                    debitVal = null;
                }
                else if (soldeSign == "DB" && creditVal.HasValue && !debitVal.HasValue)
                {
                    debitVal = creditVal;
                    creditVal = null;
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
            bool btkAnchorsImplausible = false;

            // BTL ("Extrait de compte") : le numero de compte n'apparait que dans l'en-tete
            // ("Numero de Compte : 8901769223"), jamais reimprime a cote des transactions -
            // contrairement au heuristique generique ExtractAccountNumber(joined) plus bas qui,
            // applique ligne par ligne, capte a tort des numeros de reference (10-20 chiffres) au
            // fil des transactions et ecrase ce numero par erreur. Fige ici, avant la boucle, pour
            // que ces lignes ne l'ecrasent plus (voir le garde correspondant plus bas).
            bool isBtlDoc = fullText.Contains("TUNISO-LIBYENNE", StringComparison.OrdinalIgnoreCase)
                || Regex.IsMatch(fullText, @"\bBTL\b", RegexOptions.IgnoreCase);
            if (isBtlDoc)
            {
                var btlAccountMatch = Regex.Match(fullText, @"Num[ée]ro\s*de\s*Compte\s*:?\s*(\d{6,20})", RegexOptions.IgnoreCase);
                if (btlAccountMatch.Success)
                    lastSeenAccountNumber = btlAccountMatch.Groups[1].Value;
            }

            // Tunisian Saudi Bank (TSB) : le libelle "Compte N :" est parfois lu correctement par
            // l'OCR (ex. EXTRAIT TSB.pdf), parfois tronque a 1-2 chiffres (ex. extrait PSP
            // 2025.pdf, "Compte N : 1"). Dans ce dernier cas, le compte se retrouve fiable dans le
            // RIB (20 chiffres, sans prefixe "TN" contrairement aux autres banques) : positions
            // [5..17] (13 chiffres) apres les 2 chiffres banque + 3 chiffres agence, avant les 2
            // chiffres de cle - verifie sur RIB "21014014404700244107" -> compte "0144047002441",
            // qui correspond bien au numero de compte imprime sur EXTRAIT TSB.pdf pour ce meme
            // client (PROFESSIONAL SERVICE PARTS).
            // Le motif isole "TSB" (sans "Tunisian Saudi Bank" en toutes lettres) declenchait a tort
            // ce sous-format sur des releves d'une AUTRE banque des qu'une transaction mentionnait
            // un commercant/point de vente nomme "TSB" (ex. "TSB BANK GAMMARTH" sur un releve
            // ATTIJARI, ou "TSB" n'est que le nom du point de vente, pas la banque du client) -
            // TOUT le document basculait alors sur le parsing specifique TSB, produisant un resultat
            // totalement incorrect (transactions fusionnees, banque et periode faux). Restreindre
            // aux premiers caracteres du document n'est pas fiable : l'ordre des pages dans le texte
            // OCR concatene n'est pas garanti (verifie sur ce meme cas, ou le texte de la derniere
            // page apparaissait tres tot dans fullText). Le motif isole "TSB" n'est donc retenu que
            // si aucune autre banque connue et sans ambiguite n'est deja identifiee dans le document.
            bool hasUnambiguousOtherBankName = Regex.IsMatch(fullText, @"ATTIJARI", RegexOptions.IgnoreCase);
            bool isTsbDoc = Regex.IsMatch(fullText, @"Tunisi\w*\s+Saudi\s+Bank", RegexOptions.IgnoreCase)
                || (!hasUnambiguousOtherBankName && Regex.IsMatch(fullText, @"\bTSB\b", RegexOptions.IgnoreCase))
                || fullText.Contains("Ma banque et plus", StringComparison.OrdinalIgnoreCase);
            if (isTsbDoc)
            {
                var tsbAccountMatch = Regex.Match(fullText, @"Compte\s*N[°o]?\s*:?\s*(\d{6,20})", RegexOptions.IgnoreCase);
                if (tsbAccountMatch.Success && tsbAccountMatch.Groups[1].Value.Length >= 6)
                {
                    lastSeenAccountNumber = tsbAccountMatch.Groups[1].Value;
                }
                else
                {
                    // Sous-format "Relevé de Compte" (releve_compte (1).pdf) : le RIB (20
                    // chiffres) suit l'entete "R.I.B" sur une ligne separee, pas immediatement
                    // apres un label "RIB :" - on retombe donc sur le premier bloc de 20 chiffres
                    // isole du document, fiable ici (aucun autre nombre a 20 chiffres attendu
                    // avant le RIB dans ce sous-format).
                    var tsbRibMatch = Regex.Match(fullText, @"RIB\s*:?\s*(\d{20})", RegexOptions.IgnoreCase);
                    if (!tsbRibMatch.Success)
                        tsbRibMatch = Regex.Match(fullText, @"(?<!\d)(\d{20})(?!\d)");
                    if (tsbRibMatch.Success)
                        lastSeenAccountNumber = tsbRibMatch.Groups[1].Value.Substring(5, 13);
                }
            }

            // TSB a DEUX sous-formats bien distincts :
            // - "Extrait de compte" (EXTRAIT TSB.pdf, extrait PSP 2025.pdf) : entete "... Débit
            //   Crédit Solde" - CHAQUE ligne reimprime le solde courant signe ("<montant> DB|CR"),
            //   utilisable pour corriger un sens Débit/Crédit devine a tort par le fallback
            //   generique (voir plus bas).
            // - "Relevé de Compte" (TSB_relevé.pdf) : entete "... Débit Crédit" SANS colonne Solde
            //   - aucune ligne n'a de solde courant imprime. Appliquer la meme correction par
            //   comparaison de solde y a ete teste et corrompt des transactions au hasard (le
            //   dernier nombre decimal d'une ligne n'y est jamais un solde, juste le montant lui-
            //   meme ou un fragment de texte fusionne) : la correction est donc reservee au premier
            //   sous-format via ce drapeau.
            bool tsbHasRunningSoldeColumn = isTsbDoc
                && Regex.IsMatch(fullText, @"D[ée]bit[ \t]+Cr[ée]dit[ \t]+Solde", RegexOptions.IgnoreCase);
            decimal? tsbPrevSignedSolde = null;
            bool tsbPrevWasDebit = true;

           
            decimal? btlStructuralClosingBalance = null;
            if (isBtlDoc)
            {

                var closingMatches = Regex.Matches(fullText,
                    @"^\s*TND\s*$\r?\n(-?\d{1,3}(?:[ .,]?\d{3})*[.,]\d{2,3})\s+(-?\d{1,3}(?:[ .,]?\d{3})*[.,]\d{2,3})\s*$",
                    RegexOptions.IgnoreCase | RegexOptions.Multiline);
                foreach (Match m in closingMatches)
                {
                    decimal debitVal = ParseAmount(m.Groups[1].Value);
                    decimal creditVal = ParseAmount(m.Groups[2].Value);
                    btlStructuralClosingBalance = creditVal != 0 ? creditVal : -debitVal;
                }
            }


            int? documentYear = null;
            bool isBiat = fullText.Contains("BIAT", StringComparison.OrdinalIgnoreCase);
            bool isWifak = fullText.Contains("WIFAK", StringComparison.OrdinalIgnoreCase);

            // Sous-format "Wifak relevé" (ex. RELEVEE SOGEPA 03-2024.pdf), distinct du "Wifak
            // extrait" (deja fonctionnel, ne pas toucher) : signale par "Relevé de Compte" dans
            // l'en-tete, contrairement a "Extrait de Compte" pour l'autre sous-format.
            bool isWifakReleve = isWifak && Regex.IsMatch(fullText, @"Relev[ée]\s+de\s+Compte", RegexOptions.IgnoreCase);

            // Ancres Débit/Crédit calculees des le depart a partir de la repartition reelle des
          
            if (isWifakReleve)
            {
                var (wifakInferredDebit, wifakInferredCredit) = InferAnchorsFromAmountPositions(rows);
                if (wifakInferredDebit.HasValue) debitAnchor = wifakInferredDebit;
                if (wifakInferredCredit.HasValue) creditAnchor = wifakInferredCredit;
            }

            
            bool isBiatExtraitSignedMontant = isBiat
                && Regex.IsMatch(fullText, @"Date\s+valeur", RegexOptions.IgnoreCase)
                && Regex.IsMatch(fullText, @"R[ée]f[ée]rence", RegexOptions.IgnoreCase)
                && !Regex.IsMatch(fullText, @"D[ée]bit.{0,20}Cr[ée]dit", RegexOptions.IgnoreCase | RegexOptions.Singleline);

            // ATTIJARI : les lignes de transaction ne portent que "DD MM" (jour/mois, sans annee -
            // ex. "02 01"), et le champ "Au : DD/MM/YYYY" imprime en en-tete (date de cloture du
            // releve) est trop proche du code-barres/logo pour etre lu de facon fiable par l'OCR
            // (souvent tronque en "Au : 0"). Source bien plus fiable et systematiquement lisible :
            // "SOLDE AU DD/MM/YYYY" (solde d'ouverture), toujours date de la VEILLE du debut de
            // periode du releve - on en deduit l'annee (et le mois, pour le cas particulier ou la
            // periode change d'annee, ex. solde au 31/12/2019 -> releve de janvier 2020) SANS
            // dependre du champ "Au :" illisible. Verifie en priorite (avant le fallback generique
            // ci-dessous, trop permissif et qui captait a tort un "2019" trouve ailleurs dans le
            // texte OCR bruite, produisant des dates de transaction toutes fausses d'un an).
            var attijariSoldeAuMatch = fullText.Contains("ATTIJARI", StringComparison.OrdinalIgnoreCase)
                ? Regex.Match(fullText, @"SOLDE\s+AU\s+(\d{2})[/\-.](\d{2})[/\-.](\d{4})", RegexOptions.IgnoreCase)
                : Match.Empty;
            // Sous-format ATTIJARI "EXTRAIT DE COMPTE" (distinct de "RELEVE DE COMPTE" ci-dessus,
            // peut couvrir plusieurs annees, ex. 26/01/2023 au 26/11/2024) : l'en-tete "Du :
            // DD/MM/YYYY Au : DD/MM/YYYY" est ici lu correctement par l'OCR (pas de code-barres a
            // proximite comme sur l'autre sous-format) - utilise en repli quand "SOLDE AU" est
            // absent. Donne l'annee de DEBUT de periode ; le suivi d'annee ligne a ligne
            // (attijariRollingYear, plus bas dans la boucle principale) prend le relais pour les
            // transactions qui basculent dans l'annee suivante au fil du releve.
            var attijariDuAuMatch = (!attijariSoldeAuMatch.Success && fullText.Contains("ATTIJARI", StringComparison.OrdinalIgnoreCase))
                ? Regex.Match(fullText, @"\bDu\s*:?\s*\d{2}[/\-.]\d{2}[/\-.](\d{4})", RegexOptions.IgnoreCase)
                : Match.Empty;
            if (attijariSoldeAuMatch.Success
                && int.TryParse(attijariSoldeAuMatch.Groups[2].Value, out int attijariSoldeMonth)
                && int.TryParse(attijariSoldeAuMatch.Groups[3].Value, out int attijariSoldeYear))
            {
                documentYear = attijariSoldeMonth == 12 ? attijariSoldeYear + 1 : attijariSoldeYear;
            }
            else if (attijariDuAuMatch.Success && int.TryParse(attijariDuAuMatch.Groups[1].Value, out int attijariDuYear))
            {
                documentYear = attijariDuYear;
            }
            else
            {
            var dateMatch = Regex.Match(fullText, @"\b(\d{1,2})\s+(\d{1,2})\s+(\d{4})\b");
            if (dateMatch.Success && int.TryParse(dateMatch.Groups[3].Value, out int year))
                documentYear = year;
            else if (Regex.Match(fullText, @"\b\d{1,2}[-.]\s?[A-Za-zÀ-ÿ]{3,9}[-.]\s?(\d{4})\b") is var hyphenDateMatch
                && hyphenDateMatch.Success && int.TryParse(hyphenDateMatch.Groups[1].Value, out year))
            {
                // ABC : les dates completes n'apparaissent que dans le libelle des transactions
                // (ex. "03-SEP-2025"), jamais sous forme "DD MM YYYY" espacee ni "SOLDE AU" - sans
                // ce cas, seul le dernier fallback (tres permissif, voir plus bas) s'appliquait et
                // pouvait capter par erreur un fragment sans rapport ailleurs dans le document.
                documentYear = year;
            }
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
            }

            // Garde-fou : une annee implausible (ex. ABC, ou ce dernier fallback matchait a tort un
            // fragment de 3 chiffres ailleurs dans le document, produisant "documentYear=107" puis
            // des dates de transaction du type "08/09/0107") ne doit jamais etre utilisee - mieux
            // vaut retomber sur le defaut existant de NormalizeDate (DateTime.Now.Year quand
            // defaultYear est null) qu'une annee absurde. Verification generique, sans condition de
            // banque : une annee hors de cette plage n'est jamais correcte pour aucun releve ici.
            if (documentYear.HasValue && (documentYear.Value < 2000 || documentYear.Value > 2100))
                documentYear = null;

            // ATTIJARI "EXTRAIT DE COMPTE" (voir attijariDuAuMatch plus haut) peut couvrir
            // plusieurs annees (ex. 26/01/2023 au 26/11/2024) - un seul documentYear global ne
            // suffit alors pas, puisque les dates "DD/MM" (sans annee) doivent basculer sur
            // l'annee suivante au fil des lignes. Suivi ligne a ligne : demarre a documentYear
            // (annee de DEBUT de periode), incremente des qu'un mois plus petit que le precedent
            // est rencontre (retour de decembre a janvier).
            int? attijariRollingYear = documentYear;
            int attijariLastMonth = 0;

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
                        if (isBiat || isBiatExtraitSignedMontant) t = ConvertFrenchAbbrevDates(t);
                        if (isAttijari) t = Regex.Replace(t, @"^\s*_\s*(?=\d)", "-");
                        return t;
                    })
                    .ToList();
                var cellTexts = MergeLoneSignCells(cellTextsRaw);

                string joined = string.Join(" ", cellTexts);
                joined = StripPrintArtifacts(joined);
                if (isBna) Console.WriteLine("[BNA-DEBUG] Cellules: " + string.Join(" || ", cellTexts.Select((c, ci) => $"[{ci}]='{c}'")));
                if (isQnb || isAlBarakaDoc || isBh || isAtb || isAbcBank || isBiat || isAttijari || isBtk || isWifak) Console.WriteLine($"[DIAG-CELLS] hasSignedAmounts={hasSignedAmounts} debitAnchor={debitAnchor} creditAnchor={creditAnchor} soldeAnchor={soldeAnchor} dateAnchor={dateAnchor} | " + string.Join(" || ", cellTexts.Select((c, ci) => $"[{ci}]='{c}'")));
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

                    // Variante "FAH DISTRIBUTION" (et similaires) : dates JJMMAA compactes, sans
                    // slash (ex. "050126 VRST. 2924352 060126 1 100,000"). BhCompactDatePattern
                    // n'accepte que jour 01-31 / mois 01-12, ce qui empeche un numero de reference
                    // a 6 chiffres (ex. "292435") d'etre pris a tort pour la date valeur.
                    if (!bhMatch.Success)
                        bhMatch = Regex.Match(joined,
                            $@"^({BhCompactDatePattern})\s+(.+?)\s+({BhCompactDatePattern})\s*(-?\d[\d\s.,]*\d|\d)$");

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
                if (!string.IsNullOrWhiteSpace(account)
                    && !(isBtlDoc && !string.IsNullOrWhiteSpace(lastSeenAccountNumber))
                    && !(isTsbDoc && !string.IsNullOrWhiteSpace(lastSeenAccountNumber)))
                    lastSeenAccountNumber = account;

                var ribMatch = Regex.Match(joined, @"TN\d{2}[\s\d]{15,25}");
                if (ribMatch.Success) lastSeenRib = Regex.Replace(ribMatch.Value, @"\s+", "").Trim();
               
                // "... du compte au : JJ/MM/AAAA" (ex. releves STB, pied de page "50106 du compte
                // au: 31/03/2025 -808.669 ...") : rappel de la date d'edition du releve, jamais une
                // transaction - sans cette exclusion, la date qu'elle contient etait prise pour
                // une date d'operation et son montant (le solde comptable de cloture, souvent
                // negatif) pour un debit/credit.
                if (Regex.IsMatch(joined, @"Type\s*d['’]op[ée]ration|Montant\s*Min|Montant\s*Max|Date\s*D[ée]but|Date\s*Fin\b|Agence\s*:|Intitul[ée]\s*de\s*compte|Solde\s*actuel\s*:|du\s+compte\s+au\s*:", RegexOptions.IgnoreCase))
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
                // BTK (format "mobile") : "Solde au 30/09/2024" est la ligne du solde d'ouverture
                // (une VALEUR, jamais un titre de colonne) mais contient quand meme le mot "Solde"
                // - sans cette exclusion, sa position (tres a gauche des vraies colonnes
                // Débit/Crédit/Solde) devenait a tort l'ancre soldeAnchor, ce qui faisait ensuite
                // reconnaitre a tort des numeros de reference (ex. "161269") proches de cette
                // fausse ancre comme des montants TND tronques (voir le "bare digit repair"
                // plus bas).
                var soldeCell = cells.FirstOrDefault(c => Regex.IsMatch(c.Text, @"\bSolde\b", RegexOptions.IgnoreCase)
                    && !(isBtk && Regex.IsMatch(c.Text, @"Solde\s+au\s+\d", RegexOptions.IgnoreCase)));
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
                    // ABC (entete "(-) Débit/Crédit (+)") : une seule colonne de montant signe,
                    // PAS deux colonnes distinctes - "Débit" et "Crédit" sont ici detectes dans la
                    // MEME cellule d'entete (debitCell == creditCell, meme objet). Sans ce
                    // garde-fou, le test de proximite plus bas (Math.Abs(...) < 40) declenchait une
                    // reinference de secours (InferAnchorsFromAmountPositions) qui fabriquait deux
                    // fausses colonnes Débit/Crédit a partir du bruit de bas de page (mentions
                    // legales, "68.000.000 TND", etc.) - un numero de reference proche de la fausse
                    // ancre Débit (ex. "006000" dans "REGLEMENT TPE REF 006000") etait alors capte
                    // a tort comme un second montant, place en Crédit. En laissant
                    // debitAnchor/creditAnchor nuls ici, AssignAmounts bascule sur son chemin
                    // "montant unique signe" (hasSignedAmounts), correct pour ce format.
                    bool isCombinedSignedColumn = hasSignedAmountsHeader
                        && debitCell != null && creditCell != null && debitCell == creditCell;

                    // Wifak relevé : la position du mot d'en-tete ne correspond pas a la position
                    // reelle des colonnes de chiffres (voir InferAnchorsFromAmountPositions
                    // ci-dessus, deja calculee au debut de la fonction) -- on ne laisse donc pas
                    // une relecture d'en-tete l'ecraser par une valeur peu fiable.
                    if (!isCombinedSignedColumn)
                    {
                        if (debitCell != null && !isWifakReleve) debitAnchor = debitCell.Left;
                        if (creditCell != null && !isWifakReleve) creditAnchor = creditCell.Left;
                    }
                    if (soldeCell != null) soldeAnchor = soldeCell.Left;
                    if (dateCell != null) dateAnchor = dateCell.Left;
                    if (montantCell != null) montantAnchor = montantCell.Left;

                    // BTK (format "mobile", ex. Zouheir mobil BTK.pdf) : sur ce format, le mot
                    // d'en-tete "Débit" est repere a une position qui correspond en realite a la
                    // colonne Libellé (x<900), pas a la vraie colonne numerique Débit - alors que
                    // sur les 3 autres formats BTK connus, cette detection tombe toujours dans la
                    // zone x~1000-1500. Signale ici (drapeau memorise plus bas dans la boucle) pour
                    // desactiver uniquement le "bare digit repair" (qui reprenait a tort un numero
                    // de reference comme "161269" comme montant TND tronque) SANS reinferer les
                    // ancres elles-memes - une tentative de reinference a cree une regression
                    // differente (tout en Crédit) car ce document n'a pas de vraie colonne Crédit
                    // sur cette page et l'ecart le plus large detecte etait en fait la colonne
                    // Solde, pas Crédit.
                    if (isBtk && debitAnchor.HasValue && debitAnchor.Value < 900)
                        btkAnchorsImplausible = true;

                    if (!isWifakReleve && debitAnchor.HasValue && creditAnchor.HasValue && Math.Abs(debitAnchor.Value - creditAnchor.Value) < 40)
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
                        // TSB : "Solde au : <date ouverture> <montant>" est reimprime tel quel en
                        // en-tete de CHAQUE page (contexte, pas une cloture) - sans cette garde,
                        // la derniere page ecrasait SoldeFinal avec le solde d'OUVERTURE au lieu du
                        // solde de cloture reel (jamais reimprime sous ce libelle dans ce format).
                        if (!(isTsbDoc && soldeAuVal == current.SoldeInitial))
                            current.SoldeFinal = soldeAuVal;
                    }
                    continue;
                }

                // Tolere un code devise entre parentheses juste apres "Initial" (ex. BTK : "Solde
                // initial (TND): 27 196,527") : sans ce groupe optionnel, seul le declencheur bare
                // "\bSolde\s+Initial\b" matchait (case ci-dessous), le montant n'etait jamais
                // capture et SoldeInitial restait a 0. Strictement additif : un "Solde Initial :"
                // sans parenthese continue de matcher exactement comme avant.
                var soldeInitMatch = Regex.Match(joined, @"\bSolde\s+Initial\s*(?:\([A-Za-zÀ-ÿ]{2,10}\))?\s*:?\s*([-+]?\d{1,3}(?:[ .,]?\d{3})*[.,]\d{2,3})", RegexOptions.IgnoreCase); if (soldeInitMatch.Success || Regex.IsMatch(joined, @"\bSolde\s+Initial\b", RegexOptions.IgnoreCase))
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

                // BTL : "Solde départ au" / "Solde fin au" sont suivis directement du montant, la
                // date (ex. "07 NOV 23") atterrissant sur sa propre ligne/cellule apres le decoupage
                // du tableau - contrairement au format ci-dessus (biatSoldeDepartMatch) qui exige
                // une date numerique DD/MM/YYYY immediatement apres "au". Sans ce cas, ces lignes
                // ne matchaient jamais et SoldeInitial/SoldeFinal restaient vides pour BTL.
                if (isBtlDoc)
                {
                    var btlSoldeDepartMatch = Regex.Match(joined, @"Solde\s+d[ée]part\s+au\s+(-?\d{1,3}(?:[ .,]?\d{3})*[.,]\d{2,3})", RegexOptions.IgnoreCase);
                    if (btlSoldeDepartMatch.Success)
                    {
                        if (current != null)
                        {
                            current.RawSectionText = sectionRawText;
                            sections.Add(current);
                        }
                        sectionRawText = joined + "\n";

                        decimal btlDepartInit = ParseAmount(btlSoldeDepartMatch.Groups[1].Value);
                        current = new BankAccountSection
                        {
                            AccountNumber = lastSeenAccountNumber,
                            Rib = string.IsNullOrWhiteSpace(lastSeenRib) ? documentRib : lastSeenRib,
                            Currency = ExtractCurrency(fullText),
                            SoldeInitial = btlDepartInit
                        };
                        previousSolde = btlDepartInit;
                        pendingLibelleBuffer = "";
                        pendingDate = "";
                        continue;
                    }

                    var btlSoldeFinMatch = Regex.Match(joined, @"Solde\s+fin\s+au\s+(-?\d{1,3}(?:[ .,]?\d{3})*[.,]\d{2,3})", RegexOptions.IgnoreCase);
                    if (btlSoldeFinMatch.Success)
                    {
                        if (current != null) current.SoldeFinal = ParseAmount(btlSoldeFinMatch.Groups[1].Value);
                        continue;
                    }

                    // Autre sous-format BTL, "RELEVE DE COMPTE MENSUEL" (ex. BTL 02.pdf, BTL.pdf) :
                    // la toute premiere ligne du tableau est "<date> Solde au: <date debut> <date
                    // fin> <debit> <credit>" - le solde de report, pas une vraie transaction. Sans ce
                    // cas elle finissait comptee comme transaction #0 et SoldeInitial restait vide.
                    if (current == null)
                    {
                        var btlOpeningMatch = Regex.Match(joined,
                            @"Solde\s+au\s*:?\s*\d{2}[/\-]\d{2}[/\-]\d{4}.*?(-?\d{1,3}(?:[ .,]?\d{3})*[.,]\d{2,3})\s+(-?\d{1,3}(?:[ .,]?\d{3})*[.,]\d{2,3})\s*$",
                            RegexOptions.IgnoreCase);
                        if (btlOpeningMatch.Success)
                        {
                            decimal btlOpeningDebit = ParseAmount(btlOpeningMatch.Groups[1].Value);
                            decimal btlOpeningCredit = ParseAmount(btlOpeningMatch.Groups[2].Value);
                            decimal btlOpeningInit = btlOpeningCredit != 0 ? btlOpeningCredit : -btlOpeningDebit;
                            current = new BankAccountSection
                            {
                                AccountNumber = lastSeenAccountNumber,
                                Rib = string.IsNullOrWhiteSpace(lastSeenRib) ? documentRib : lastSeenRib,
                                Currency = ExtractCurrency(fullText),
                                SoldeInitial = btlOpeningInit
                            };
                            previousSolde = btlOpeningInit;
                            sectionRawText = joined + "\n";
                            pendingLibelleBuffer = "";
                            pendingDate = "";
                            continue;
                        }
                    }
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

                // BIAT (sous-format "RELEVE DE COMPTE MENSUEL", ex. Pages_All (1).pdf) : la ligne
                // "TOTAUX <debit> <credit>" imprimee par la banque en pied de page inclut le solde
                // de depart ("SOLDE AU ...", capture ci-dessus dans biatSoldeAuMatch) comme s'il
                // s'agissait d'un mouvement debit/credit de la page - ce n'est PAS une transaction
                // (BankExcelExporter continue de calculer le sien a partir des transactions reelles
                // uniquement) mais BankExcelExporter l'utilise en PRIORITE pour l'affichage du total
                // BIAT afin de correspondre au chiffre imprime sur le releve. Capture generique
                // (n'importe quelle banque pourrait avoir "TOTAUX x y") mais reservee a isBiat pour
                // eviter tout effet de bord sur un autre format partageant cette meme fonction.
                if (isBiat)
                {
                    var biatTotauxMatch = Regex.Match(joined,
                        @"\bTOTAUX?\s+(-?\d{1,3}(?:[ .,]?\d{3})*[.,]\d{2,3})\s+(-?\d{1,3}(?:[ .,]?\d{3})*[.,]\d{2,3})",
                        RegexOptions.IgnoreCase);
                    if (biatTotauxMatch.Success)
                    {
                        if (current != null)
                        {
                            current.TotalDebit = ParseAmount(biatTotauxMatch.Groups[1].Value);
                            current.TotalCredit = ParseAmount(biatTotauxMatch.Groups[2].Value);
                        }
                        continue;
                    }
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

                // Attijari (ex. PSP EXTRAIT) : "Solde Veille" est le solde reporte, reimprime en
                // tete de CHAQUE page. Seule la toute premiere occurrence (avant toute vraie
                // transaction) vaut solde initial ; les suivantes ne sont ni un solde final ni
                // une transaction et doivent etre ignorees.
                if (isAttijari)
                {
                    var attijariSoldeVeilleMatch = Regex.Match(joined,
                        @"Solde\s+Veille\s*:?\s*(-?\d{1,3}(?:[ .,]?\d{3})*[.,]\d{2,3})", RegexOptions.IgnoreCase);
                    if (attijariSoldeVeilleMatch.Success)
                    {
                        if (current == null)
                        {
                            current = new BankAccountSection
                            {
                                AccountNumber = lastSeenAccountNumber,
                                Rib = string.IsNullOrWhiteSpace(lastSeenRib) ? documentRib : lastSeenRib,
                                Currency = ExtractCurrency(fullText),
                                SoldeInitial = ParseAmount(attijariSoldeVeilleMatch.Groups[1].Value)
                            };
                            previousSolde = current.SoldeInitial;
                            sectionRawText = joined + "\n";
                            pendingLibelleBuffer = "";
                            pendingDate = "";
                        }
                        continue;
                    }
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

                // Ligne recapitulative generique "TOTAL <debit> <credit>" ou "TOTAUX <debit>
                // <credit>" (ex. releves STB : "TOTAL 33 207.861 40 860.745") : un total de
                // periode imprime en pied de tableau, jamais une transaction - contrairement a
                // totalMouvementsMatch ci-dessus qui exige la formulation precise "Total des
                // mouvements", cette variante couvre le mot seul, utilise par d'autres banques.
                // Sans cette exclusion, cette ligne (aucune date, mais deux montants complets)
                // finissait comptee comme une transaction a part entiere, doublant quasiment le
                // total reel du compte (Debit ET Credit tous les deux non nuls). Ancree en debut
                // de ligne et sans date : ne peut pas confondre une vraie transaction avec un
                // libelle commencant par "Total".
                var genericTotalLineMatch = Regex.Match(joined,
                    @"^\s*TOTA(?:UX|L)\b.{0,30}?(-?\d{1,3}(?:[ .,]?\d{3})*[.,]\d{2,3})\s+(-?\d{1,3}(?:[ .,]?\d{3})*[.,]\d{2,3})",
                    RegexOptions.IgnoreCase);
                if (genericTotalLineMatch.Success && !ClassifyDateAnywhereRegex.IsMatch(joined))
                {
                    if (current != null)
                    {
                        current.TotalDebit = ParseAmount(genericTotalLineMatch.Groups[1].Value);
                        current.TotalCredit = ParseAmount(genericTotalLineMatch.Groups[2].Value);
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

                // Voir attijariRollingYear plus haut : ce releve ATTIJARI peut couvrir plusieurs
                // annees. Des qu'un mois retombe en dessous du precedent (retour de decembre a
                // janvier), on avance l'annee suivie et on recalcule la date de cette ligne avec
                // la bonne annee (documentYear seul, fige au debut de periode, restait bloque sur
                // la premiere annee pour toutes les lignes suivantes).
                if (isAttijari && !string.IsNullOrEmpty(normalizedDate)
                    && DateTime.TryParseExact(normalizedDate, "dd/MM/yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var attijariRowDate))
                {
                    int rowMonth = attijariRowDate.Month;
                    if (attijariLastMonth != 0 && rowMonth < attijariLastMonth - 6)
                        attijariRollingYear = (attijariRollingYear ?? attijariRowDate.Year) + 1;
                    attijariLastMonth = rowMonth;

                    if (attijariRollingYear.HasValue && attijariRollingYear.Value != attijariRowDate.Year)
                        normalizedDate = new DateTime(attijariRollingYear.Value, attijariRowDate.Month, attijariRowDate.Day).ToString("dd/MM/yyyy");
                }

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

                if (isBiatExtraitSignedMontant && string.IsNullOrEmpty(normalizedDate))
                {
                    normalizedDate = GetDateFromAdjacentCellPairs(cellTexts, documentYear);
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


                // BIAT "EXTRAIT" a montant signe unique (ex. EXTRAIT (1)biat.pdf, export TEMENOS) :
                // le separateur de milliers "espace" du Montant (ex. "-1 350,000") est parfois lu
                // par l'OCR comme DEUX cellules distinctes ("-1" et "350,000") a cause de
                // l'espacement visuel entre les groupes de chiffres. ParseAmount sait deja
                // reconstituer un montant "espace"-separe recu en un seul texte (voir plus bas),
                // mais seulement si les deux fragments sont d'abord reunis ici : le petit fragment
                // ("-1", 1 a 3 chiffres, aucune decimale) ne matche jamais AmountRegex seul et etait
                // jusqu'ici purement perdu (ni le millier ni le signe negatif n'atteignaient le
                // montant final). Reserve a isBiatExtraitSignedMontant : les autres formats BIAT
                // (colonnes Debit/Credit separees, deja fonctionnels) n'utilisent jamais cette
                // reunification.
                if (isBiatExtraitSignedMontant)
                {
                    for (int i = 0; i < mergedWithPos.Count - 1; i++)
                    {
                        if (mergedWithPos[i].IsContinuationDetail || mergedWithPos[i + 1].IsContinuationDetail)
                            continue;
                        string first = mergedWithPos[i].Text.Trim();
                        string second = mergedWithPos[i + 1].Text.Trim();
                        if (Regex.IsMatch(first, @"^-?\d{1,3}$") && AmountRegex.IsMatch(second))
                        {
                            mergedWithPos[i + 1] = (mergedWithPos[i + 1].Left, first + " " + mergedWithPos[i + 1].Text, mergedWithPos[i + 1].IsContinuationDetail);
                            mergedWithPos.RemoveAt(i);
                            i--;
                        }
                    }
                }

                // "DONT <libelle>: <montant>" (ex. "EFFET 818 DONT TVA: 0,570") est un sous-detail
                // descriptif du libelle - l'equivalent francais de "of which" - jamais le vrai
                // montant Debit/Credit de la transaction (qui reste plus loin sur la ligne, apres
                // la colonne Date valeur). Sans cette exclusion, ce sous-detail (souvent le seul
                // nombre a virgule complet de la ligne quand le vrai montant est mal lu par l'OCR,
                // ex. reduit a un simple "0") etait pris a tort pour LE montant de la transaction.
                // Verifie seulement sur les 2 cellules precedentes : le motif est toujours
                // immediatement adjacent au montant qu'il annote - sans risque pour les autres
                // lignes/banques, ou "DONT" n'apparait jamais juste avant un vrai montant.
                bool IsPrecededByDontClause(int index)
                {
                    for (int back = 1; back <= 2 && index - back >= 0; back++)
                    {
                        if (Regex.IsMatch(mergedWithPos[index - back].Text, @"\bDONT\b", RegexOptions.IgnoreCase))
                            return true;
                    }
                    return false;
                }

                var amountCandidates = mergedWithPos
                    .Select((c, idx) => (Cell: c, Index: idx))
                    .Where(x => !x.Cell.IsContinuationDetail && AmountRegex.IsMatch(x.Cell.Text) && !IsPrecededByDontClause(x.Index))
                    .Select(x => new { Left = x.Cell.Left, Value = ParseAmount(AmountRegex.Match(x.Cell.Text).Value) })
                    .ToList();

                
               amountCandidates = amountCandidates
                    .GroupBy(c => new { c.Value, ZoneLeft = c.Left / 20 })
                    .Select(g => g.First())
                    .ToList();
                if ((debitAnchor.HasValue || creditAnchor.HasValue || soldeAnchor.HasValue) && !btkAnchorsImplausible)
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
                    // ABC : la ligne d'en-tete "Du/Au JJ.MM.AAAA - JJ.MM.AAAA" (periode du releve,
                    // avant "Solde de départ") est parfois OCRisee avec la 2e date isolee dans sa
                    // propre cellule (ex. "31.05.2026") - AmountRegex la lit alors a tort comme un
                    // montant decimal ("31.05"), et la 1ere date comme une date de transaction
                    // valide, ce qui ouvrait ici une fausse section (AccountNumber vide, un seul
                    // "montant" fantome) avant meme la vraie section demarree par abcSoldeDepartMatch
                    // plus haut. ABC demarre TOUJOURS sa table par une ligne "Solde de départ" -
                    // jusque-la, current doit rester nul (la ligne est ignoree) plutot que d'etre
                    // cree par ce repli generique.
                    if (!isAbcBank && !string.IsNullOrEmpty(normalizedDate) && amountCandidates.Count > 0)
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

                // Wifak relevé : pied de page/coordonnees bancaires (souvent en francais+arabe
                // melanges, sur plusieurs pages) qui se retrouvait colle au libelle de la
                // derniere transaction lue avant lui, faute d'etre reconnu comme bruit. Egalement
                // les lignes "Solde ..." intermediaires (solde reporte en bas de page, texte du
                // milieu parfois illisible par l'OCR) qui ne sont jamais des transactions.
                bool wifakReleveNoiseHit = isWifakReleve && (
                    Regex.IsMatch(joined, @"^\s*Solde\b", RegexOptions.IgnoreCase)
                    || Regex.IsMatch(joined,
                        @"wifakbank\.com|Matricule\s+Fiscal|R\.C\s*:|Centre\s+d['’]affaires|Centre\s+d['’]Appel|" +
                        @"Wifak\s+International\s+Bank|M[ée]diateur\s+bancaire|reclamations\.clients|" +
                        @"Cher\s*\(e\)\s*client|d[ée]lai\s+qui\s+ne\s+d[ée]passe\s+pas|est\s+[àa]\s+votre\s+[ée]coute",
                        RegexOptions.IgnoreCase));
                if (wifakReleveNoiseHit) biatInNoiseZone = true;

                // ABC : pied de page legal (bilingue francais/arabe, "Tout débit ponctuel...",
                // "conformément à l'article 672 du Code de Commerce", coordonnees Bank ABC, etc.)
                // colle a la derniere transaction du releve et contenant, dans la meme zone, le
                // solde de cloture encadre ("Le solde ... 6.380,751") - sans ce filtre, ce dernier
                // finissait par generer une fausse transaction (le solde de cloture interprete a
                // tort comme un Crédit). Une fois ce bruit rencontre, biatInNoiseZone reste actif
                // jusqu'a la fin (pas de nouvelle transaction ensuite sur ce releve), ce qui
                // empeche aussi les lignes suivantes du meme pied de page de se recoller au
                // libelle de la derniere transaction reelle.
                bool abcNoiseHit = isAbcBank && Regex.IsMatch(joined,
                    @"Tout\b.{0,20}?d[ée]bit\s+ponctuel|d[ée]passement\s+.{0,15}?[ée]ventuel|" +
                    @"En\s+cas\s+de\s+contestation|conform[ée]ment.{0,15}?article\s+672|Code\s+de\s+Commerce|" +
                    @"Merci\s+d['’]avoir\s+choisi\s+Bank\s+ABC|" +
                    @"www\.bank-abc\.com|Swift\s+Code|Bank\s+ABC\s*\(Arab\s+Banking\s+Corporation|" +
                    @"adh[ée]rente.{0,25}syst[èe]me\s+de\s+garantie|D[ée]cret\s+n[°o]?\s*\d{4}-\d+|" +
                    @"\bArticle\s+30\b|que\s+vous\s+avez\s+approuv[ée]|document\s+g[ée]n[ée]r[ée]\s+informatiquement",
                    RegexOptions.IgnoreCase);
                if (abcNoiseHit) biatInNoiseZone = true;

                bool isAccountHeaderNoise = LibelleHeaderFooterNoiseRegex.IsMatch(joined);

                // Ligne majoritairement illisible par l'OCR (texte arabe non reconnu, remplace par
                // des "?" - frequent sur les en-tetes/pieds de page bilingues, ex. ATTIJARI) :
                // aucune information exploitable, et laissee telle quelle elle produisait de
                // fausses transactions fantomes (date et montant pris au hasard dans ce charabia).
                // Verifiee uniquement quand la ligne est assez longue (>=15 caracteres non-espace)
                // pour ne jamais confondre un vrai libelle court contenant un point d'interrogation
                // isole avec du bruit OCR.
                string joinedNonSpace = joined.Replace(" ", "");
                bool isMostlyUnreadable = joinedNonSpace.Length >= 15
                    && joinedNonSpace.Count(ch => ch == '?') >= joinedNonSpace.Length / 3;

                bool isNoise = Regex.IsMatch(joined, @"\b(Totaux?|Page\s*\d|Solde\s*(Initial|Final)|[ée]v[èe]nements?|\(\*\)|Solde\s*\(\w+\)\s*au|BTK@?DIRECT|https?://\S+|Report|A\s+[Rr]eporter)", RegexOptions.IgnoreCase)
                     || biatNoiseHit
                     || bnaNoiseHit
                     || albarakaNoiseHit
                     || wifakReleveNoiseHit
                     || abcNoiseHit
                     || isRepeatedBoilerplate
                     || isAccountHeaderNoise
                     || isMostlyUnreadable
                     || (isQnb && Regex.IsMatch(joined, @"Cette\s+d[ée]claration\s+sera\s+consid[ée]r[ée]e|dans\s+votre\s+situation\s+de\s+compte|Pour\s+toute\s+r[ée]clamation", RegexOptions.IgnoreCase));

                // Une ligne majoritairement illisible (voir isMostlyUnreadable ci-dessus) doit etre
                // ignoree meme quand un fragment de chiffres qu'elle contient (ex. un numero de
                // document/code agence comme "01091562") a ete pris a tort pour une date ET un
                // montant valides par les heuristiques generiques plus bas - sinon elle produit une
                // fausse transaction complete (date et montant inventes) au lieu d'etre sautee.
                // Verifiee ici, AVANT toute utilisation de normalizedDate/amountCandidates,
                // contrairement au isNoise generique plus bas qui n'est lu que si aucune date n'a
                // ete detectee sur la ligne.
                if (isMostlyUnreadable) continue;

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

                    // BTK : un chiffre de detail imprime DANS la zone du libelle (ex. le montant de
                    // la TVA repris a cote de "TVA/commission", tres a gauche des colonnes Débit/
                    // Crédit reelles) generait une fausse transaction independante au lieu de
                    // rester une precision textuelle du libelle de l'operation "COM & TVA ..." deja
                    // comptabilisee juste au-dessus. Repere par la position : un montant dont AUCUN
                    // candidat n'est proche (>300px) des ancres Débit/Crédit reelles ne peut pas etre
                    // un vrai montant de transaction sur ce releve BTK. Ne change rien pour un
                    // montant meme legerement proche d'une des deux ancres (comportement inchange).
                    if (isBtk && isPureAmountLine && debitAnchor.HasValue && creditAnchor.HasValue)
                    {
                        bool allFarFromAmountColumns = true;
                        foreach (var cand in amountCandidates)
                        {
                            int distD = Math.Abs((int)cand.Left - debitAnchor.Value);
                            int distC = Math.Abs((int)cand.Left - creditAnchor.Value);
                            if (distD <= 300 || distC <= 300) { allFarFromAmountColumns = false; break; }
                        }
                        if (allFarFromAmountColumns) isPureAmountLine = false;
                    }

                    // Attijari : une ligne de continuation comme "DONT TVA: 0,161" contient un
                    // montant mais n'EST pas un montant de transaction -- c'est du texte de
                    // libelle qui mentionne un chiffre. On ne l'exclut du texte de continuation
                    // (ci-dessous) que si aucune transaction n'attend deja son montant
                    // (pendingDate vide) et qu'il reste du texte substantiel une fois le montant
                    // retire ; sinon (ex. la ligne "1,012" qui complete reellement l'operation en
                    // attente) le comportement existant est inchange.
                    if (isAttijari && isPureAmountLine && string.IsNullOrEmpty(pendingDate))
                    {
                        string withoutAmount = AmountRegex.Replace(joined, "").Trim();
                        if (withoutAmount.Length > 3)
                            isPureAmountLine = false;
                    }

                    if (isPureAmountLine)
                    {
                        if (string.IsNullOrEmpty(pendingDate))
                        {
                            if (!isBiat && !isBiatExtraitSignedMontant && current.Transactions.Count > 0 && !isNoise)

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
                        AssignAmounts(tx2, amountCandidates, debitAnchor, creditAnchor, soldeAnchor, montantAnchor, isBtk, ref previousSolde, hasSignedAmounts, out var soldeCourantTX, btkAnchorsImplausible);
                        ApplyMovementFallback(tx2, soldeAvant2, soldeCourantTX);
                        current.Transactions.Add(tx2);
                        if (isBna) Console.WriteLine($"[BNA-DEBUG] Transaction creee (ligne sans date propre) : Date='{tx2.Date}' Libelle='{tx2.Libelle}' Debit={tx2.Debit} Credit={tx2.Credit}");
                        pendingDate = "";
                        biatInNoiseZone = false;
                        continue;
                    }

                    if (!isPureAmountLine)
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
                    // Une ligne de header/pied de page (adresse, mentions legales, bandeau
                    // repete a chaque page...) peut par coincidence contenir une date -- ex.
                    // une date d'impression du document -- ce qui la faisait jusqu'ici passer
                    // pour le debut (ou la fin, via "[MONTANT MANQUANT]") d'une transaction, en
                    // l'absence de tout montant. isNoise/biatInNoiseZone sont deja calcules plus
                    // haut sur cette meme ligne (motifs generiques + repetition structurelle via
                    // isRepeatedBoilerplate, PAS une phrase specifique a un releve) et deja
                    // utilises juste en dessous pour la branche a 1 seule cellule : on applique
                    // ici la meme garde, par coherence, avant de fabriquer un pendingDate/une
                    // transaction orpheline a partir de cette ligne.
                    if (isNoise || biatInNoiseZone)
                    {
                        continue;
                    }

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

                        string textOnly = string.Join(" ", cellTexts.Skip(1).Where(c =>
                            !AmountRegex.IsMatch(isAmenDocument ? StripAmenEchoDate(c) : c)
                            && !LibelleHeaderFooterNoiseRegex.IsMatch(c)));
                        if (isAmenDocument) textOnly = StripAmenEchoDate(textOnly);

                        // BTL : une ligne sans montant reconnu (OCR ayant tronque le chiffre, ex.
                        // "1 0" au lieu de "1 000,000") est normalement mise en attente puis
                        // prefixee au libelle de la PROCHAINE transaction - correct quand elle en
                        // est reellement la suite, mais quand elle commence par un mot-cle de
                        // transaction BTL a part entiere (Versement, Retrait, Reglement Cheque,
                        // Virement, Encaissement), c'est une operation distincte qui doit rester sa
                        // propre ligne meme si la transaction suivante partage la meme date
                        // (frequent : plusieurs operations le meme jour). Sans cette garde, elle se
                        // retrouvait fondue dans le libelle d'une transaction sans rapport, sans
                        // meme etre signalee.
                        bool isBtlStandaloneOrphan = isBtlDoc
                            && Regex.IsMatch(textOnly,
                                @"^\s*\d*\s*(Versement|Retrait|Reglement\s+Cheque|Virement|Encaissement)",
                                RegexOptions.IgnoreCase);

                        if (isBtlStandaloneOrphan)
                        {
                            current.Transactions.Add(new Transaction
                            {
                                Date = normalizedDate,
                                Libelle = (textOnly.Trim() + " [MONTANT A VERIFIER - lecture OCR incertaine]").Trim()
                            });
                            pendingDate = "";
                            pendingLibelleBuffer = "";
                        }
                        else
                        {
                            pendingDate = normalizedDate;
                            pendingLibelleBuffer = (pendingLibelleBuffer + " " + textOnly).Trim();
                        }
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

                // BIAT EXTRAIT (montant signe unique) : cette ligne porte sa propre date ET son
                // propre montant, donc pendingDate/pendingLibelleBuffer ne lui appartiennent pas
                // s'ils viennent d'une operation precedente dont le montant n'a jamais ete
                // reconnu (ex. montant illisible en OCR) -- sinon le libelle de cette operation
                // orpheline se retrouve concatene devant celui de l'operation courante. On la
                // publie donc a part, montant manquant signale, plutot que de la faire disparaitre
                // dans la description de la transaction suivante.
                // Wifak relevé : une operation peut arriver a la fois avec sa propre date ET son
                // propre montant alors qu'une operation precedente (meme date, montant illisible
                // par l'OCR, ex. "560" au lieu de "5 000,000") est encore en attente -- on la
                // publie donc a part elle aussi, meme quand les deux dates sont identiques
                // (contrairement au cas BIAT EXTRAIT ci-dessus ou l'inegalite de date reste
                // exigee, comportement deja valide et volontairement inchange).
                bool shouldFlushPendingOrphan = !string.IsNullOrEmpty(pendingDate)
                    && ((isBiatExtraitSignedMontant && pendingDate != normalizedDate) || isWifakReleve);
                if (shouldFlushPendingOrphan)
                {
                    current.Transactions.Add(new Transaction
                    {
                        Date = pendingDate,
                        Libelle = (pendingLibelleBuffer.Trim() + " [MONTANT MANQUANT - a verifier manuellement]").Trim()
                    });
                    pendingLibelleBuffer = "";
                }

                string fullDescription = (pendingLibelleBuffer + " " + description).Trim();
                pendingLibelleBuffer = "";
                pendingDate = "";

                // TSB (releve_compte (1).pdf) : la ligne "Total ... / الجديد الرصيد" (nouveau
                // solde) et la mention legale de cloture ("... considérons approuvé la totalité
                // des mouvements ... sans restrictions ni réserves ...", bilingue francais/arabe)
                // ont chacune une date et un montant valides et se faisaient donc compter a tort
                // comme de vraies transactions.
                if (isTsbDoc && IsTsbClosingBoilerplate(fullDescription))
                {
                    continue;
                }

                // TSB : a un saut de page, le numero de reference d'une transaction (ex.
                // "2101400337") atterrit parfois seul sur sa propre ligne au lieu de rester
                // rattache au libelle de la transaction precedente, entrainant la creation d'une
                // fausse transaction fantome (libelle = uniquement ce numero, sans aucun texte).
                if (isTsbDoc && Regex.IsMatch(fullDescription.Trim(), @"^\d{6,15}$"))
                {
                    continue;
                }

                // BTK (format "mobile") : IsMergedRow ne connait pas la convention BTK "dernier
                // montant = solde courant" et, avec des ancres Débit/Crédit peu fiables sur cette
                // page (voir btkAnchorsImplausible), comptait a tort une ligne normale (montant
                // reel + solde) comme fusionnee - d'ou les "[LIGNE FUSIONNEE]" sur des transactions
                // simples. Laisse alors la ligne passer par AssignAmounts (qui gere deja ce cas via
                // soldeCourant + resolution par variation de solde, voir plus haut).
                if (!(isBtk && btkAnchorsImplausible) && IsMergedRow(amountCandidates, debitAnchor, creditAnchor, soldeAnchor))
                {
                    if (isBiat)
                    {
                        Console.WriteLine($"[DIAG-MERGE] libelle='{fullDescription}' debitAnchor={debitAnchor} creditAnchor={creditAnchor} soldeAnchor={soldeAnchor} candidates=" +
                            string.Join(" || ", ((IEnumerable<dynamic>)amountCandidates).Select(c => $"Left={c.Left} Value={c.Value}")));
                    }
                    foreach (var splitTx in SplitMergedRow(normalizedDate, fullDescription, amountCandidates, debitAnchor, creditAnchor))
                        current.Transactions.Add(splitTx);
                    biatInNoiseZone = false;
                    continue;
                }

                decimal? soldeAvantTx = previousSolde;
                var tx = new Transaction { Date = normalizedDate, Libelle = fullDescription };
                AssignAmounts(tx, amountCandidates, debitAnchor, creditAnchor, soldeAnchor, montantAnchor, isBtk, ref previousSolde, hasSignedAmounts, out  var soldeCourantTx, btkAnchorsImplausible);
                ApplyMovementFallback(tx, soldeAvantTx, soldeCourantTx);

                // BTK : "COM & TVA ..." (commission + TVA sur retrait carte, virement, etc.) est
                // TOUJOURS un frais preleve au client, donc toujours un Débit - jamais un Crédit.
                // La classification generique (distance aux ancres Débit/Crédit) le place parfois a
                // tort en Crédit quand le petit montant de la commission tombe, par coincidence,
                // plus proche geometriquement de la colonne Crédit. Correction par mot-cle, uniquement
                // quand le libelle contient explicitement ce motif "COM ... TVA".
                if (isBtk && tx.Credit.HasValue && !tx.Debit.HasValue
                    && Regex.IsMatch(tx.Libelle ?? "", @"\bCOM\b.{0,10}&?.{0,10}\bTVA\b", RegexOptions.IgnoreCase))
                {
                    tx.Debit = tx.Credit;
                    tx.Credit = null;
                }

                if (tsbHasRunningSoldeColumn && (tx.Debit.HasValue || tx.Credit.HasValue))
                {
                    // Le solde courant est le dernier montant decimal reconnaissable de la ligne
                    // (colonne la plus a droite) - fiable meme quand la cellule Débit/Crédit
                    // elle-meme est illisible par l'OCR (ex. "1 0" au lieu de "1 095,000"). Le
                    // marqueur DB/CR qui le suit est parfois lui aussi corrompu (ex. lu "8" au lieu
                    // de "DB") : dans ce cas on garde le signe de la ligne precedente (les
                    // changements de signe DB<->CR sont rares et alors correctement lus).
                    var tsbAmountMatches = Regex.Matches(joined, @"-?\d{1,3}(?:[ .,]?\d{3})*[.,]\d{2,3}");
                    if (tsbAmountMatches.Count > 0)
                    {
                        var lastAmountMatch = tsbAmountMatches[tsbAmountMatches.Count - 1];
                        string afterAmount = joined.Substring(lastAmountMatch.Index + lastAmountMatch.Length);
                        bool tsbIsDebit = Regex.IsMatch(afterAmount, @"\bCR\b", RegexOptions.IgnoreCase)
                            ? false
                            : (Regex.IsMatch(afterAmount, @"\bDB\b", RegexOptions.IgnoreCase) || tsbPrevWasDebit);
                        decimal tsbSoldeVal = ParseAmount(lastAmountMatch.Value);
                        decimal tsbSignedSolde = tsbIsDebit ? -Math.Abs(tsbSoldeVal) : Math.Abs(tsbSoldeVal);

                        if (tsbPrevSignedSolde.HasValue)
                        {
                            decimal delta = tsbSignedSolde - tsbPrevSignedSolde.Value;
                            if (Math.Abs(delta) > 0.001m)
                            {
                                bool expectDebit = delta < 0;
                                bool actualIsCredit = tx.Credit.HasValue;
                                bool actualIsDebit = tx.Debit.HasValue;
                                if ((expectDebit && actualIsCredit) || (!expectDebit && actualIsDebit))
                                {
                                    (tx.Debit, tx.Credit) = (tx.Credit, tx.Debit);
                                }
                            }
                        }
                        tsbPrevSignedSolde = tsbSignedSolde;
                        tsbPrevWasDebit = tsbIsDebit;
                    }
                }

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

            if (isAbcBank)
            {
                // ABC : le pied de page legal (bilingue) peut rester colle au libelle de la
                // derniere transaction (fusionne en continuation avant meme cette boucle, voir
                // MergeContinuationLines) malgre le filtre de bruit ci-dessus - on le retire donc
                // ici aussi, apres coup. Meme motif d'ancrage que abcNoiseHit plus haut.
                var abcBoilerplateAnchor = new Regex(
                    @"\s*(Tout\b.{0,20}?d[ée]bit\s+ponctuel|d[ée]passement\s+.{0,15}?[ée]ventuel|" +
                    @"En\s+cas\s+de\s+contestation|conform[ée]ment.{0,15}?article\s+672|Code\s+de\s+Commerce|" +
                    @"Merci\s+d['’]avoir\s+choisi\s+Bank\s+ABC|" +
                    @"www\.bank-abc\.com|Swift\s+Code|Bank\s+ABC\s*\(Arab\s+Banking\s+Corporation|" +
                    @"adh[ée]rente.{0,25}syst[èe]me\s+de\s+garantie|D[ée]cret\s+n[°o]?\s*\d{4}-\d+|" +
                    @"\bArticle\s+30\b|que\s+vous\s+avez\s+approuv[ée]|document\s+g[ée]n[ée]r[ée]\s+informatiquement).*$",
                    RegexOptions.IgnoreCase);

                foreach (var sec in sections)
                {
                    foreach (var tx in sec.Transactions)
                        tx.Libelle = abcBoilerplateAnchor.Replace(tx.Libelle ?? "", "").Trim();

                    // Le solde de cloture encadre ("Le solde ... 6.380,751") est imprime dans le
                    // meme bloc que ce pied de page et se retrouvait, avant filtrage, transforme a
                    // tort en fausse transaction Crédit - desormais supprime en amont par
                    // abcNoiseHit. On le recalcule ici a partir du mouvement reel des transactions
                    // plutot que de tenter de le relire depuis ce bloc bruite.
                    sec.Transactions.RemoveAll(t => string.IsNullOrWhiteSpace(t.Libelle));

                    if (!sec.SoldeFinal.HasValue && sec.SoldeInitial.HasValue)
                    {
                        decimal totalD = sec.Transactions.Sum(t => t.Debit ?? 0);
                        decimal totalC = sec.Transactions.Sum(t => t.Credit ?? 0);
                        sec.SoldeFinal = sec.SoldeInitial.Value - totalD + totalC;
                    }
                }
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

            if (isBtlDoc && btlStructuralClosingBalance.HasValue && sections.Count > 0)
            {
                var lastSection = sections[sections.Count - 1];
                if (!lastSection.SoldeFinal.HasValue)
                    lastSection.SoldeFinal = btlStructuralClosingBalance;
            }

            
            if (isTsbDoc)
            {
                foreach (var sec in sections)
                    sec.Transactions.RemoveAll(tx => IsTsbClosingBoilerplate(tx.Libelle ?? ""));
            }

            
            if (isBtk)
            {
                var btkTotalMatches = Regex.Matches(fullText,
                    @"Total\s+des\s+op[ée]rations\s+(-?\d{1,3}(?:[ .,]?\d{3})*[.,]\d{2,3})\s+(-?\d{1,3}(?:[ .,]?\d{3})*[.,]\d{2,3})",
                    RegexOptions.IgnoreCase);
                if (btkTotalMatches.Count > 0 && sections.Count > 0)
                {
                    var lastMatch = btkTotalMatches[btkTotalMatches.Count - 1];
                    var lastBtkSection = sections[sections.Count - 1];
                    if (!lastBtkSection.TotalDebit.HasValue)
                        lastBtkSection.TotalDebit = ParseAmount(lastMatch.Groups[1].Value);
                    if (!lastBtkSection.TotalCredit.HasValue)
                        lastBtkSection.TotalCredit = ParseAmount(lastMatch.Groups[2].Value);
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
        private List<BankAccountSection> ExtractBhAccountSections(string fullText, List<TableRow> rows)
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

            const string BhDatePattern = @"\d{4}-\d{1,2}-\d{1,2}|\d{1,2}[/\-.]\d{1,2}(?:[/\-.]\d{2,4})?|" + BhCompactDatePattern;

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

            // Une "ligne" = une ligne de la table reconstruite par position (rows), pas une ligne
            // de texte brut OCR : sur ce type de releve, l'ordre de lecture OCR separe parfois le
            // montant (colonne Debit/Credit) de sa ligne Date/Libelle (voir commentaire dans Parse
            // au niveau de l'appel a ExtractBhAccountSections). Toute la logique ci-dessous
            // (regex, garde-fous SOLDE/CLOTURE...) reste inchangee : elle opere juste sur ces
            // lignes reconstruites au lieu du texte brut.
            var lines = rows.Select(r => string.Join(" ", r.Cells.Select(c => c.Text))).ToArray();

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

                // Decoupage par POSITION des dates plutot qu'un regex monolithique (bhLineRegex,
                // conserve plus bas pour Solde Ouverture/etc.) : sur les libelles contenant des
                // fragments de reference isoles (ex. "VERSEMENT TPE 8 4 160126 1 62,400"), un seul
                // regex avec libelle paresseux + date-valeur optionnelle capturait a tort le
                // fragment "8" comme debut du groupe montant (qui n'a pas de longueur minimale),
                // avalant ensuite la vraie date-valeur ET le vrai montant dans un seul bloc
                // illisible -> transaction ajoutee sans montant (IsLikelyGarbledBhDate le rejetait
                // ensuite). Chercher les dates par position (BhDatePattern ne matche que des jours
                // 01-31/mois 01-12, donc jamais un fragment de reference isole) elimine cette
                // ambiguite : 1ere date = date operation, derniere date trouvee ensuite = date
                // valeur (si presente), tout ce qui suit = montant.
                var bhDateTokenRegex = new Regex(BhDatePattern, RegexOptions.IgnoreCase);
                var bhDateMatches = bhDateTokenRegex.Matches(line);
                if (bhDateMatches.Count == 0)
                {
                    Console.WriteLine($"[BH-WARN] Ligne non reconnue (ignorée) : {line}");
                    continue;
                }

                string dateOp = bhDateMatches[0].Value;
                string bhRestOfLine = line.Substring(bhDateMatches[0].Index + bhDateMatches[0].Length).TrimStart();

                string rawLibelle;
                string montantRaw;
                var bhRestDateMatches = bhDateTokenRegex.Matches(bhRestOfLine);
                if (bhRestDateMatches.Count > 0)
                {
                    var valeurDateMatch = bhRestDateMatches[bhRestDateMatches.Count - 1];
                    rawLibelle = bhRestOfLine.Substring(0, valeurDateMatch.Index).Trim();
                    montantRaw = bhRestOfLine.Substring(valeurDateMatch.Index + valeurDateMatch.Length).Trim();
                }
                else
                {
                    var bhTrailingAmountMatch = Regex.Match(bhRestOfLine, @"(-?\d[\d\s.,]*\d|\d)\s*$");
                    if (bhTrailingAmountMatch.Success)
                    {
                        rawLibelle = bhRestOfLine.Substring(0, bhTrailingAmountMatch.Index).Trim();
                        montantRaw = bhTrailingAmountMatch.Value.Trim();
                    }
                    else
                    {
                        rawLibelle = bhRestOfLine;
                        montantRaw = "";
                    }
                }

                if (string.IsNullOrWhiteSpace(rawLibelle) || string.IsNullOrWhiteSpace(montantRaw))
                {
                    Console.WriteLine($"[BH-WARN] Ligne non reconnue (ignorée) : {line}");
                    continue;
                }

                string libelle = ExtractBhLibelle(rawLibelle);

                var tx = new Transaction
                {
                    Date = NormalizeDate(dateOp, documentYear),
                    Libelle = libelle
                };

                // Sur certains scans, l'OCR perd le "/" d'une deuxieme date de ligne (Date valeur)
                // ou le lit comme un chiffre ("1" ou "7") - ex. "24/02/2026" devient "25022026" ou
                // "2410212026". bhLineRegex, tres permissif sur le montant final (n'importe quelle
                // suite de chiffres), capturait alors cette date deformee comme SI c'etait le
                // montant de la transaction (une valeur enorme et fausse). On la detecte ici et on
                // laisse la transaction sans montant plutot que d'inventer une valeur absurde.
                if (!IsLikelyGarbledBhDate(montantRaw))
                {
                    decimal montant = ParseAmount(montantRaw);

                    // Garde-fou complementaire : sur les portions les plus degradees du scan, l'OCR
                    // fusionne parfois plusieurs jetons (dates ET montants) d'une meme ligne en une
                    // seule chaine que ParseAmount interprete comme un montant demesure (ex. deux
                    // dates concatenees avec un vrai montant). Aucune operation courante de ce type
                    // de compte (frais, prelevement, virement) n'atteint le million de dinars : au-
                    // dela, c'est presque certainement un artefact OCR, pas un vrai montant - on
                    // laisse alors la transaction sans montant plutot que d'additionner une valeur
                    // absurde au total.
                    if (Math.Abs(montant) < 1_000_000m)
                    {
                        if (IsBhDebitLibelle(libelle))
                            tx.Debit = montant;
                        else
                            tx.Credit = montant;
                    }
                }

                current.Transactions.Add(tx);
            }

            current.RawSectionText = fullText;
            sections.Add(current);
            return sections;
        }

        // Detecte une date "JJ/MM/AAAA" dont l'OCR a perdu les deux "/" (8 chiffres, ex.
        // "25022026") ou les a lus comme un chiffre "1" ou "7" (10 chiffres, ex. "2410212026",
        // "2470272026") - jamais un vrai montant BH plausible (aucune commission/prelevement de ce
        // releve n'atteint des millions de dinars). Jour/mois/annee valides exiges pour eviter tout
        // faux positif sur un montant a 8 chiffres genuinement enorme.
        private static bool IsLikelyGarbledBhDate(string raw)
        {
            string s = raw.Trim();
            var m = Regex.Match(s, @"^(\d{2})(\d{2})(\d{4})$");
            if (!m.Success) m = Regex.Match(s, @"^(\d{2})[17](\d{2})[17](\d{4})$");
            if (!m.Success) return false;

            if (!int.TryParse(m.Groups[1].Value, out int day) || day < 1 || day > 31) return false;
            if (!int.TryParse(m.Groups[2].Value, out int month) || month < 1 || month > 12) return false;
            if (!int.TryParse(m.Groups[3].Value, out int year) || year < 2000 || year > 2099) return false;
            return true;
        }

        private string NormalizeDate(string raw, int? defaultYear = null)
        {
            raw = raw.Trim();

            // Confusion OCR tres frequente (Tesseract) : le chiffre "0" lu comme la lettre "o"
            // minuscule quand il est colle a un autre chiffre (ex. "o1" au lieu de "01", "17 o1"
            // au lieu de "17 01") - sans cette normalisation, ce genre de cellule ne correspondait
            // a aucun format de date ci-dessous, et la transaction entiere se retrouvait donc
            // fondue dans le libelle de la transaction precedente au lieu d'etre reconnue comme
            // une nouvelle ligne. Uniquement "o"/"O" directement adjacent a un chiffre (jamais un
            // mot ordinaire), donc sans risque pour un libelle contenant par ailleurs cette lettre.
            raw = Regex.Replace(raw, @"(?<=\d)[oO](?=\d)|(?<=\d)[oO]\b|\b[oO](?=\d)", "0");

            string candidate = raw;

            if (Regex.IsMatch(raw, @"^\d{1,2}\s+\d{1,2}\s*$") && !raw.Contains('/') && !raw.Contains('-'))
            {
                candidate = Regex.Replace(raw.Trim(), @"\s+", "/");
            }

            // Un numero de cheque/reference a 8 chiffres (ex. "10069302", "29112074") ressemble
            // a une date "JJMMAAAA" sans separateurs, mais son "annee" tombe hors de toute plage
            // plausible pour un releve (jamais dans le futur au-dela d'un an, jamais avant 1990)
            // - sans ce garde-fou, DateTime.TryParseExact l'accepte quand meme (.NET n'a pas de
            // limite d'annee < 9999) et produit une date absurde (ex. "10/06/9302", "29/11/2074"
            // pour un chiffre d'annee mal lu par l'OCR, ex. "0" lu "7").
            if (raw.Length == 8 && !raw.Contains('/') && !raw.Contains('-') && !raw.Contains('.')
                && int.TryParse(raw.Substring(4, 4), out int rawYear) && rawYear >= 1990 && rawYear <= DateTime.Now.Year + 1)
                candidate = $"{raw.Substring(0, 2)}/{raw.Substring(2, 2)}/{raw.Substring(4, 4)}";
            else if (raw.Length == 4 && !raw.Contains('/') && !raw.Contains('-') && !raw.Contains('.') && !raw.Contains(' '))
                candidate = $"{raw.Substring(0, 2)}/{raw.Substring(2, 2)}";
            // Format compact "JJMMAA" (6 chiffres, sans separateur) : certains releves BH
            // (ex. "FAH DISTRIBUTION") impriment les dates ainsi plutot qu'en JJ/MM/AAAA.
            // Valide jour/mois avant conversion pour eviter de confondre avec un numero de
            // reference a 6 chiffres (ex. "292435", mois="24" invalide -> rejete ci-dessous).
            else if (raw.Length == 6 && !raw.Contains('/') && !raw.Contains('-') && !raw.Contains('.') && !raw.Contains(' ')
                && int.TryParse(raw.Substring(0, 2), out int compactDay) && compactDay >= 1 && compactDay <= 31
                && int.TryParse(raw.Substring(2, 2), out int compactMonth) && compactMonth >= 1 && compactMonth <= 12)
                candidate = $"{raw.Substring(0, 2)}/{raw.Substring(2, 2)}/{raw.Substring(4, 2)}";

        
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

            // "dd M"/"d MM"/"d M" (mois ou jour a un seul chiffre) en plus de "dd MM" : sur
            // certains formats (ex. ATTIJARI, colonne date "DD MM" sans annee, cellule mois lue par
            // l'OCR "1" au lieu de "01"), "dd MM" echouait car "MM" exige strictement 2 chiffres,
            // ce qui faisait perdre la date de la transaction (donc la ligne entiere, absorbee a
            // tort dans le libelle de la transaction precedente). Variantes "/" EGALEMENT
            // necessaires (pas seulement espace) : le bloc juste au-dessus convertit deja tout
            // "DD MM" (espace) en "DD/M" (slash) avant d'arriver ici - sans "dd/M" etc., aucun des
            // formats espace ci-dessus ne pouvait plus matcher (candidate ne contient plus
            // d'espace a ce stade), ce qui annulait entierement cette correction.
            var formatsNoYear = new[] { "dd/MM", "dd-MM", "dd.MM", "dd MM", "dd M", "d MM", "d M", "dd/M", "d/MM", "d/M" };
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

        // BIAT EXTRAIT uniquement (voir isBiatExtraitSignedMontant) : GetNormalizedDateFromCells
        // ne teste que les 3 premieres cellules jointes depuis le debut de la ligne (colonne
        // "Date valeur"). Si l'OCR perd le mot du mois sur CETTE colonne precise (ex. "19" / "24"
        // sans "JAN", le mot etant absent de la ligne au lieu d'etre simplement colle), la colonne
        // "Date opération" plus loin sur la meme ligne (ex. "22 JAN" / "24") reste, elle, intacte.
        // On cherche donc une paire de cellules adjacentes valides n'importe ou dans la ligne,
        // en dernier recours seulement (jamais si GetNormalizedDateFromCells/GetDateFromAnyCell a
        // deja trouve une date) : preferer une date legerement decalee (Date opération plutot que
        // Date valeur) reste bien moins dommageable que perdre l'operation entiere.
        private string GetDateFromAdjacentCellPairs(List<string> cellTexts, int? defaultYear)
        {
            for (int i = 0; i < cellTexts.Count - 1; i++)
            {
                string normalized = NormalizeDate(cellTexts[i] + " " + cellTexts[i + 1], defaultYear);
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
        private void AssignAmounts(Transaction tx, dynamic amountCandidates, int? debitAnchor, int? creditAnchor, int? soldeAnchor, int? montantAnchor, bool isBtk, ref decimal? previousSolde , bool hasSignedAmounts, out decimal? soldeCourant, bool btkAnchorsImplausible = false)
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
                else if (isBtk && amountCandidates.Count == 1 && soldeAnchor.HasValue)
                {
                    // BTK : quand le montant Débit/Crédit de la ligne est illisible par l'OCR (ou
                    // fusionne avec une autre ligne) et qu'il ne reste qu'UN seul montant detecte,
                    // c'est presque toujours le solde courant (colonne la plus a droite) et non un
                    // vrai montant - le laisser passer par la classification Débit/Crédit
                    // ci-dessous (basee sur la distance aux ancres) le faisait atterrir a tort en
                    // Crédit des que sa position tombait, par coincidence, plus proche de l'ancre
                    // Crédit que de l'ancre Débit. Detecte ici via la distance a l'ancre Solde ;
                    // laisse Débit/Crédit vides, ApplyMovementFallback (appele par l'appelant)
                    // deduit ensuite le sens du mouvement a partir de la variation du solde
                    // (Nouveau solde = Ancien solde - Débit + Crédit), conformement a l'integrite
                    // comptable attendue.
                    dynamic onlyCand = null;
                    foreach (var c in amountCandidates) onlyCand = c;
                    int distDebitOnly = debitAnchor.HasValue ? Math.Abs((int)onlyCand.Left - debitAnchor.Value) : int.MaxValue;
                    int distCreditOnly = creditAnchor.HasValue ? Math.Abs((int)onlyCand.Left - creditAnchor.Value) : int.MaxValue;
                    int distSoldeOnly = Math.Abs((int)onlyCand.Left - soldeAnchor.Value);
                    if (distSoldeOnly <= distDebitOnly && distSoldeOnly <= distCreditOnly)
                    {
                        soldeCourant = onlyCand.Value;
                        classifiable = new List<dynamic>();
                    }
                }

                foreach (var cand in classifiable)
                {
                    int distDebit = Math.Abs((int)cand.Left - debitAnchor.Value);
                    int distCredit = Math.Abs((int)cand.Left - creditAnchor.Value);
                    int distSolde = soldeAnchor.HasValue ? Math.Abs((int)cand.Left - soldeAnchor.Value) : int.MaxValue;

                    // BTK (format "mobile") : quand les ancres Débit/Crédit du texte d'en-tete se
                    // sont averees peu fiables pour cette page (voir btkAnchorsImplausible, repere
                    // plus haut), la comparaison de distance ci-dessous placerait ce montant du
                    // mauvais cote presque a chaque fois. On le passe donc en "ambigu" : plus bas,
                    // il sera tranche par la variation du solde (Nouveau solde = Ancien solde -
                    // Débit + Crédit), independante de la position, deja utilisee pour les autres
                    // cas ambigus.
                    if (isBtk && btkAnchorsImplausible)
                    {
                        ambiguous.Add(cand);
                        continue;
                    }

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
                        // Ni signe ni delta de solde pour trancher (mise en page sans colonne
                        // Solde, ex. Wifak releve Date/Libelle/DateValeur/Debit/Credit) : au lieu
                        // de supposer Credit par defaut (biaisait systematiquement TOUS les
                        // montants Debit non signes vers Credit des que leur position tombait dans
                        // la zone de tolerance +/-15px, cf. colonnes Debit/Credit etroites et
                        // adjacentes), on retient l'ancre géométriquement la plus proche -- coherent
                        // avec la branche non-ambigue ci-dessus qui fait deja ce choix des que
                        // l'ecart depasse la tolerance.
                        int distDebitAmb = Math.Abs((int)cand.Left - debitAnchor.Value);
                        int distCreditAmb = Math.Abs((int)cand.Left - creditAnchor.Value);
                        if (distDebitAmb <= distCreditAmb) tx.Debit = value;
                        else tx.Credit = value;
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

     
        // TSB : mention legale de cloture de releve (bilingue francais/arabe, "considérons
        // approuvé la totalité des mouvements ... sans restrictions ni réserves") et ligne "Total
        // / nouveau solde" (الجديد الرصيد) - toutes deux porteuses d'une date et d'un montant
        // valides, donc comptees a tort comme transactions par le moteur generique.
        private static bool IsTsbClosingBoilerplate(string text)
        {
            return Regex.IsMatch(text,
                @"الجديد\s*الرصيد|consid[ée]rons\s+approuv[ée]|restrictions\s+ni\s+r[ée]serves",
                RegexOptions.IgnoreCase);
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
            "PRLV.", "COMMISSION", "T.V.A", "TV.A", "TVA", "COMFORC", "VIR.TN MM BQ",
            "VIR.ORDON", "PAIEMENT CHQ", "RETRAIT"
        };

        // Date "JJMMAA" compacte (6 chiffres, sans slash) utilisee par certains releves BH -
        // jour 01-31 / mois 01-12 uniquement, pour ne jamais confondre avec un numero de
        // reference a 6 chiffres (voir usages dans NormalizeDate et le parsing des lignes BH).
        private const string BhCompactDatePattern = @"(?:0[1-9]|[12]\d|3[01])(?:0[1-9]|1[0-2])\d{2}";

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
            // Comparaison insensible a la casse (ex. "Tnd" lu par l'OCR), "Dinar Tunisien" en
            // toutes lettres, et surtout \b (limites de mot) : sans elles, "EUR" matchait a tort
            // comme sous-chaine de "DEBITEUR" (terme bancaire francais courant, aucun rapport avec
            // l'euro), faisant retomber a tort l'export Excel sur le format a 2 decimales
            // (BankExcelExporter) au lieu des 3 decimales attendues pour le dinar tunisien.
            if (Regex.IsMatch(text, @"\bTND\b", RegexOptions.IgnoreCase)) return "TND";
            if (Regex.IsMatch(text, @"\bDINAR\b", RegexOptions.IgnoreCase)) return "TND";
            if (Regex.IsMatch(text, @"\bEUR\b", RegexOptions.IgnoreCase)) return "EUR";
            if (Regex.IsMatch(text, @"\bUSD\b", RegexOptions.IgnoreCase)) return "USD";

            // Aucune devise mentionnee nulle part dans le texte (ex. AMEN BQList.pdf, export brut
            // sans en-tete ni logo) : toutes les banques de cette application sont tunisiennes, TND
            // est donc le defaut logique plutot que de laisser vide (ce qui faisait retomber a tort
            // l'export Excel sur le format a 2 decimales au lieu de 3 pour le dinar tunisien).
            return "TND";
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

        // Utilisee uniquement par ConvertFrenchAbbrevDates (BIAT EXTRAIT : "05 JAN 24") : les
        // abreviations y sont en anglais (JAN, FEB, MAR...), contrairement a FrenchMonthsAbbrev
        // ci-dessus (utilise par BNA notamment) qui ne reconnait que des abreviations francaises
        // ("janv", "mars"...) et ne doit pas etre modifie pour ne pas affecter BNA.
        private static readonly Dictionary<string, string> BiatMonthsAbbrev = new(StringComparer.OrdinalIgnoreCase)
        {
            {"janv","01"}, {"jan","01"}, {"fevr","02"}, {"févr","02"}, {"feb","02"},
            {"mars","03"}, {"mar","03"}, {"avr","04"}, {"apr","04"},
            {"mai","05"}, {"may","05"}, {"juin","06"}, {"jun","06"},
            {"juil","07"}, {"jul","07"}, {"aout","08"}, {"août","08"}, {"aug","08"},
            {"sept","09"}, {"sep","09"}, {"oct","10"}, {"nov","11"}, {"dec","12"}, {"déc","12"},
        };

        private string ConvertFrenchAbbrevDates(string text)
        {
            // Cas 1 : jour + mois + annee tous dans la meme cellule (ex. "09JAN24", OCR sans
            // espaces) -> conversion numerique complete directe.
            text = Regex.Replace(text, @"(\d{1,2})\s*([A-Za-zéûÉÛ]{3,5})\.?\s*(\d{2,4})", m =>
            {
                string day = m.Groups[1].Value.PadLeft(2, '0');
                string monthRaw = m.Groups[2].Value;
                string year = m.Groups[3].Value;
                foreach (var kv in BiatMonthsAbbrev)
                    if (monthRaw.StartsWith(kv.Key, StringComparison.OrdinalIgnoreCase))
                        return $"{day}/{kv.Value}/{(year.Length == 2 ? "20" + year : year)}";
                return m.Value;
            });

            // Cas 2 : jour + mois colles sans annee dans la cellule (ex. "05JAN", l'annee "24"
            // etant dans la cellule suivante du tableau) -> on se contente d'inserer l'espace
            // jour/mois manquant. GetNormalizedDateFromCells recompose ensuite "05 JAN" + "24" en
            // "05 JAN 24", deja reconnu par NormalizeDate (format "dd MMM yy", InvariantCulture
            // reconnait nativement JAN..DEC) : aucune duplication de logique de date ici.
            text = Regex.Replace(text, @"^(\d{1,2})([A-Za-zéûÉÛ]{3,5})$", m =>
            {
                string monthRaw = m.Groups[2].Value;
                foreach (var kv in BiatMonthsAbbrev)
                    if (monthRaw.StartsWith(kv.Key, StringComparison.OrdinalIgnoreCase))
                        return $"{m.Groups[1].Value} {monthRaw}";
                return m.Value;
            });

            // Cas 3 : cellule "annee" polluee par un artefact d'impression isole (ex. "24 —")
            // qui casse le TryParseExact strict de NormalizeDate en aval. Ne retire ce parasite
            // que lorsque la cellule est purement numerique (jamais dans un libelle).
            text = Regex.Replace(text, @"^(\d{2,4})\s*[—–]\s*$", "$1");

            return text;
        }
        private string ExtractBankName(string text)
        {
            // BTK@DIRECT / btknet.com : signal d'en-tete tres specifique (reimprime sur chaque
            // page de l'export web BTK@DIRECT), verifie AVANT la boucle generique ci-dessous - sans
            // cette priorite, un simple "BNA" trouve dans une ligne de transaction (ex. un
            // libelle d'ATM tiers "ATM BNA CENTRE URBAIN", qui ne designe pas la banque du
            // relevé) faisait perdre la partie a "BNA" dans la boucle generique, meme quand "BTK"
            // apparait bien plus souvent (en-tete, URL, libelles ATM BTK) dans le meme document.
            if (text.Contains("BTK@DIRECT", StringComparison.OrdinalIgnoreCase)
                || text.Contains("btknet.com", StringComparison.OrdinalIgnoreCase))
            {
                return "Banque Tuniso-Koweitienne (BTK)";
            }
            // "ATTIJARI" verifie avant tout sigle court d'une autre banque (BNA, BTE, TSB...) :
            // ces sigles courts apparaissent couramment dans les libelles de transaction d'un
            // releve ATTIJARI (nom de commercant, agence tierce, reference de cheque) et faisaient
            // a tort basculer tout le document sur une autre banque (vu concretement sur
            // MEJRI RABEB 0380052351368-2020-01/07.pdf, mal identifies TSB puis BTE puis BNA).
            // "ATTIJARI" est un mot complet et distinctif, sans risque equivalent de collision.
            if (Regex.IsMatch(text, @"ATTIJARI", RegexOptions.IgnoreCase))
            {
                return "Attijari Bank";
            }
            // Meme garde-fou que bteNameHit dans Parse() : un releve ATB peut mentionner "BTE" en
            // interne (ex. "Encaissement CHQ-BTE ...", un cheque tire sur un compte BTE encaisse
            // par ce client ATB) sans etre lui-meme un releve BTE.
            bool looksLikeAtb = Regex.IsMatch(text, @"\bATB\b", RegexOptions.IgnoreCase)
                || text.Contains("Arab Tunisian Bank", StringComparison.OrdinalIgnoreCase);
            // Meme collision que le garde-fou looksLikeAttijariInstead plus bas (TSB) : un releve
            // ATTIJARI dont une transaction referencait "BTE" comme tiers etait mal identifie.
            bool looksLikeAttijari = Regex.IsMatch(text, @"ATTIJARI", RegexOptions.IgnoreCase);
            if (text.Contains("Banque de Tunisie et des Emirats", StringComparison.OrdinalIgnoreCase)
                || (Regex.IsMatch(text, @"\bBTE\b") && !looksLikeAtb && !looksLikeAttijari))
            {
                return "Banque de Tunisie et des Emirats (BTE)";
            }
            if (text.Contains("TUNISO-LIBYENNE", StringComparison.OrdinalIgnoreCase)
                || Regex.IsMatch(text, @"\bBTL\b", RegexOptions.IgnoreCase))
            {
                return "Banque Tuniso-Libyenne (BTL)";
            }
            // Meme garde-fou que looksLikeAtb ci-dessus : le sigle isole "TSB" (sans "Tunisian
            // Saudi Bank" en toutes lettres) matche aussi un simple libelle de transaction
            // mentionnant un commercant/point de vente nomme "TSB" (ex. "TSB BANK GAMMARTH" sur un
            // releve ATTIJARI, ou "TSB" n'est que le nom du point de vente, pas la banque du
            // client) - sans cette exclusion, ce releve ATTIJARI etait entierement mal identifie
            // comme un releve TSB (et parse avec les regles TSB, donnant un resultat faux).
            bool looksLikeAttijariInstead = Regex.IsMatch(text, @"ATTIJARI", RegexOptions.IgnoreCase);
            if (Regex.IsMatch(text, @"Tunisi\w*\s+Saudi\s+Bank", RegexOptions.IgnoreCase)
                || (!looksLikeAttijariInstead && Regex.IsMatch(text, @"\bTSB\b", RegexOptions.IgnoreCase))
                || text.Contains("Ma banque et plus", StringComparison.OrdinalIgnoreCase))
            {
                return "Tunisian Saudi Bank (TSB)";
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

            // Un releve peut mentionner le sigle d'une AUTRE banque uniquement comme reference a
            // un cheque tiers encaisse (ex. "Encaissement CHQ-BNA ...", "CHEQUE STB ...") sans que
            // cette autre banque soit celle du releve - ignore cette occurrence precise (une autre
            // occurrence du meme sigle, hors contexte "cheque", continue elle de matcher
            // normalement).
            foreach (var bank in knownBanks)
                // Sensible a la casse (comme l'ancien text.Contains(bank.Keyword) qu'il remplace) :
                // un sigle en MAJUSCULES (ex. "UIB") ne doit jamais matcher sa version minuscule
                // fortuitement presente au milieu d'un autre mot (ex. "Bourg-uib-a").
                foreach (Match m in Regex.Matches(text, Regex.Escape(bank.Keyword)))
                {
                    int start = Math.Max(0, m.Index - 15);
                    string before = text.Substring(start, m.Index - start);
                    if (Regex.IsMatch(before, @"CH[EÉÈ]?QU?E?[\s\-]*$", RegexOptions.IgnoreCase))
                        continue;
                    return bank.FullName;
                }

            return "";
        }
    }
}