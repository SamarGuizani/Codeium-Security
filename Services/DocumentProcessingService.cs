using System.Globalization;
using Codeium_Security.Factories;
using Codeium_Security.Interfaces;
using Codeium_Security.Models;
using Codeium_Security.OCR;
using Codeium_Security.Services.Calculation;
using Codeium_Security.Services.DocumentClassification;
using Codeium_Security.Services.DocumentMetadataExtraction;
using Codeium_Security.Utilities;

namespace Codeium_Security.Services
{
    public class DocumentProcessingService
    {
        private readonly IOcrService _ocrService;
        private readonly DocumentAnalysisEngine _engine;
        private readonly IEnumerable<IDocumentExtractor> _extractors;
        private readonly IDocumentClassifier _classifier;
        private readonly DocumentParserFactory _parserFactory;
        private readonly IConfiguration _configuration;
        private readonly IDocumentMetadataExtractor _metadataExtractor;
        private readonly TransactionSumCalculator _sumCalculator = new();

        public DocumentProcessingService(
            IOcrService ocrService,
            DocumentAnalysisEngine engine,
            IEnumerable<IDocumentExtractor> extractors,
            IDocumentClassifier classifier,
            DocumentParserFactory parserFactory,
            IConfiguration configuration,
            IDocumentMetadataExtractor metadataExtractor)
        {
            _ocrService = ocrService;
            _engine = engine;
            _extractors = extractors;
            _classifier = classifier;
            _parserFactory = parserFactory;
            _configuration = configuration;
            _metadataExtractor = metadataExtractor;
        }

        public async Task<OcrResult> RunOcrOnlyAsync(string filePath, string originalFileName)
        {
            var swTotal = System.Diagnostics.Stopwatch.StartNew();
            Console.WriteLine($"[TIMER] ===== DEBUT {originalFileName} =====");

            var extractor = _extractors.FirstOrDefault(e => e.CanHandle(originalFileName))
                ?? throw new NotSupportedException($"Aucun extracteur ne prend en charge le fichier '{originalFileName}'.");
            Console.WriteLine($"IMAGE UTILISEE : {filePath}");
            var ocrResult = await extractor.ExtractAsync(filePath, originalFileName);

            ocrResult.FullText = TextCleaner.RemoveInvisibleMarks(ocrResult.FullText);
            ocrResult.PageTexts = ocrResult.PageTexts.Select(TextCleaner.RemoveInvisibleMarks).ToList();

            foreach (var word in ocrResult.Words)
                word.Text = TextCleaner.RemoveInvisibleMarks(word.Text);
            foreach (var word in ocrResult.Words)
                word.Text = TextCleaner.RemoveStrayTableBorders(word.Text);

            Console.WriteLine($"[TIMER] ===== FIN {originalFileName}: {swTotal.ElapsedMilliseconds} ms TOTAL =====");
            return ocrResult;
        }

        // Deduit une ExtractionPeriod des dates min/max parmi toutes les transactions de tous
        // les comptes (Transaction.Date est deja normalise en "dd/MM/yyyy" par
        // BankDocumentParser). Retourne null si aucune date exploitable (aucune transaction,
        // ou toutes les dates sont vides/invalides).
        private static ExtractionPeriod? DerivePeriodFromTransactionDates(BankDocument bankDoc)
        {
            DateTime? min = null;
            DateTime? max = null;

            foreach (var account in bankDoc.Accounts)
            {
                foreach (var tx in account.Transactions)
                {
                    if (!DateTime.TryParseExact(tx.Date, "dd/MM/yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
                        continue;

                    if (min == null || parsed < min) min = parsed;
                    if (max == null || parsed > max) max = parsed;
                }
            }

            if (min == null || max == null)
                return null;

            return new ExtractionPeriod { Start = min.Value.ToString("dd/MM/yyyy"), End = max.Value.ToString("dd/MM/yyyy") };
        }

        private List<TextLine> GroupLines(OcrResult ocrResult)
        {
            return ocrResult.SuggestedVerticalTolerance.HasValue
                ? _engine.GroupWordsIntoLines(ocrResult.Words, ocrResult.SuggestedVerticalTolerance.Value)
                : _engine.GroupWordsIntoLines(ocrResult.Words);
        }

        public async Task<DocumentProcessingResult> ProcessFileAsync(string filePath, string originalFileName)
        {
            var ocrResult = await RunOcrOnlyAsync(filePath, originalFileName);

            var lines = GroupLines(ocrResult);
            var diagRows = _engine.BuildTable(lines);
            Console.WriteLine($"[DIAG] TextLines: {lines.Count} | TableRows: {diagRows.Count}");
            for (int i = 0; i < Math.Min(10, diagRows.Count); i++)
            {
                string cellsDump = string.Join(" || ", diagRows[i].Cells.Select(c => $"Left={c.Left}:'{c.Text}'"));
                Console.WriteLine($"[DIAG] Row {i}: {cellsDump}");
            }
            var documentType = _classifier.Classify(ocrResult.FullText);
            var parser = _parserFactory.GetParser(documentType);
            Console.WriteLine("===== TEXT SENT TO BANK PARSER START =====");
            Console.WriteLine(ocrResult.FullText);
            Console.WriteLine("===== TEXT SENT TO BANK PARSER END =====");
            object? document = parser?.Parse(ocrResult.FullText, lines);

            // Metadonnees generiques d'en-tete (nom du client, periode de l'extrait) :
            // module independant, ne modifie ni ne relit le resultat du parser ci-dessus.
            var metadata = _metadataExtractor.Extract(ocrResult.FullText, diagRows);

            // BankName : reprise directe de la valeur DEJA calculee par BankDocumentParser
            // ci-dessus (isAttijari/isBh/isUbci/ExtractBankName...) - aucune seconde detection
            // de banque n'est creee ici, conformement au principe "reutiliser l'existant".
            metadata.BankName = (document as BankDocument)?.BankName;

            // Repli Banque Zitouna (2026-09-17) : ExtractBankName (BankDocumentParser) ne connait
            // pas "ZITOUNA" dans sa table de banques, donc BankDocument.BankName reste vide pour ces
            // relevés - meme signal deja calcule en interne par BankDocumentParser (isZitouna =
            // fullText.Contains("ZITOUNA")), relu ici en couche orchestration, sans toucher au
            // parser ni a l'OCR. Necessaire pour que BankExcelExporter/ZitounaLedgerAccountClassifier
            // detectent correctement ces documents (voir ZitounaLedgerAccountClassifier.IsZitounaDocument).
            if (string.IsNullOrEmpty(metadata.BankName) && ocrResult.FullText.Contains("ZITOUNA", StringComparison.OrdinalIgnoreCase))
                metadata.BankName = "Banque Zitouna";

            // Repli Periode (voir ExtractPeriod dans GenericDocumentMetadataExtractor) : de
            // nombreux releves (ex. BTK, AMEN, QNB, BIAT scannes) n'impriment aucun texte
            // "Du ... au ...", "Periode :" etc. dans l'en-tete - seules les dates de transaction
            // individuelles sont lisibles. Quand aucun texte de periode explicite n'a ete
            // trouve, on deduit Start/End des dates min/max deja extraites par BankDocumentParser
            // (aucune nouvelle regle de parsing, lecture seule des transactions).
            if ((metadata.Period == null || (string.IsNullOrWhiteSpace(metadata.Period.Start) && string.IsNullOrWhiteSpace(metadata.Period.End)))
                && document is BankDocument bankDocForPeriod)
            {
                var derivedPeriod = DerivePeriodFromTransactionDates(bankDocForPeriod);
                if (derivedPeriod != null)
                    metadata.Period = derivedPeriod;
            }

            bool needsReview = EvaluateNeedsReview(documentType, document, ocrResult.Confidence);

            // Calcul automatique post-parsing de SUM(Debit)/SUM(Credit) uniquement (voir
            // TransactionSumCalculator) : aucune validation, aucune comparaison avec le solde,
            // aucune regle metier, et ne modifie jamais le document. Generique, s'applique a
            // toutes les banques sans logique specifique.
            var sums = document is BankDocument bankDoc ? _sumCalculator.Calculate(bankDoc) : null;

            return new DocumentProcessingResult
            {
                FileName = originalFileName,
                DetectedType = documentType.ToString(),
                PageCount = ocrResult.PageCount,
                OcrConfidence = ocrResult.Confidence,
                NeedsReview = needsReview,
                Document = document,
                Metadata = metadata,
                Sums = sums,
                DebugLines = lines.Select(l => l.FullLineText).ToList(),
                Lines = lines.Select(l => new LineOutput
                {
                    LineText = l.FullLineText,
                    Words = l.Words.Select(w => new WordCoordinate
                    {
                        Text = w.Text,
                        Confidence = w.Confidence,
                        Left = w.Left,
                        Top = w.Top,
                        Right = w.Right,
                        Bottom = w.Bottom
                    }).ToList()
                }).ToList(),

                Words = ocrResult.Words.Select(w => new WordCoordinate
                {
                    Text = w.Text,
                    Confidence = w.Confidence,
                    Left = w.Left,
                    Top = w.Top,
                    Right = w.Right,
                    Bottom = w.Bottom
                }).ToList()
            };
        }

        public string FormatAsPlainText(string fileName, List<string> pageTexts)
        {
            var sb = new System.Text.StringBuilder();

            if (pageTexts.Count <= 1)
            {
                sb.AppendLine($"****** Résultat pour {fileName} ******");
                sb.AppendLine((pageTexts.Count == 1 ? pageTexts[0] : "").Trim());
            }
            else
            {
                for (int i = 0; i < pageTexts.Count; i++)
                {
                    sb.AppendLine($"****** Résultat pour {fileName} - Page {i + 1} ******");
                    sb.AppendLine(pageTexts[i].Trim());
                    sb.AppendLine();
                }
            }

            return sb.ToString();
        }

        private static bool EvaluateNeedsReview(DocumentType documentType, object? document, float confidence)
        {
            if (confidence < 85)
                return true;

            if (documentType == DocumentType.Unknown)
                return true;

            if (document is BankDocument bank)
            {
                if (bank.Accounts.Count == 0)
                    return true;

                // Verifie chaque sous-compte : si un seul est incomplet, le document est signale pour revue
                foreach (var account in bank.Accounts)
                {
                    if (string.IsNullOrWhiteSpace(account.AccountNumber))
                        return true;
                    if (account.SoldeFinal == 0 && account.Transactions.Count == 0)
                        return true;
                }
            }

            return false;
        }

        private static string TruncateText(string text, int maxLength)
        {
            if (string.IsNullOrEmpty(text) || text.Length <= maxLength)
                return text;

            return text[..maxLength] + "...";
        }
    }
}