using Codeium_Security.Factories;
using Codeium_Security.Models;
using Codeium_Security.OCR;
using Codeium_Security.Services.DocumentClassification;
using Codeium_Security.Utilities;

namespace Codeium_Security.Services
{
    public class DocumentProcessingService
    {
        private readonly IOcrService _ocrService;
        private readonly DocumentAnalysisEngine _engine;
        private readonly PdfToImageConverter _pdfConverter;
        private readonly IDocumentClassifier _classifier;
        private readonly DocumentParserFactory _parserFactory;
        private readonly ImageFormatConverter _formatConverter;
        private readonly IConfiguration _configuration;

        public DocumentProcessingService(
            IOcrService ocrService,
            DocumentAnalysisEngine engine,
            PdfToImageConverter pdfConverter,
            IDocumentClassifier classifier,
            DocumentParserFactory parserFactory,
            ImageFormatConverter formatConverter,
            IConfiguration configuration)
        {
            _ocrService = ocrService;
            _engine = engine;
            _pdfConverter = pdfConverter;
            _classifier = classifier;
            _parserFactory = parserFactory;
            _formatConverter = formatConverter;
            _configuration = configuration;
        }

        public async Task<OcrResult> RunOcrOnlyAsync(string filePath, string originalFileName)
        {
            var swTotal = System.Diagnostics.Stopwatch.StartNew();
            Console.WriteLine($"[TIMER] ===== DEBUT {originalFileName} =====");

            if (!_pdfConverter.IsPdf(originalFileName) && _formatConverter.NeedsConversion(originalFileName))
            {
                filePath = _formatConverter.ConvertToPng(filePath);
            }

            OcrResult ocrResult;

            if (_pdfConverter.IsPdf(originalFileName))
            {
                var swConvert = System.Diagnostics.Stopwatch.StartNew();
                string pdfImagesFolder = Path.Combine("Images", "pdf_pages_" + Path.GetFileNameWithoutExtension(originalFileName));
                var pageImagePaths = _pdfConverter.ConvertPdfToImages(filePath, pdfImagesFolder);
                Console.WriteLine($"[TIMER] Conversion PDF->images: {swConvert.ElapsedMilliseconds} ms pour {pageImagePaths.Count} pages");

                var pageResults = new List<OcrResult>();
                foreach (var pageImagePath in pageImagePaths)
                {
                    var swOcr = System.Diagnostics.Stopwatch.StartNew();
                    try
                    {
                        var fileInfo = new FileInfo(pageImagePath);
                        if (!fileInfo.Exists || fileInfo.Length == 0)
                        {
                            pageResults.Add(new OcrResult { FullText = "", Confidence = 0, Words = new List<OcrWord>() });
                            continue;
                        }

                        Console.WriteLine($"[TIMER] --> Debut OCR page: {pageImagePath}");
                        pageResults.Add(await _ocrService.ExtractTextAsync(pageImagePath));
                        Console.WriteLine($"[TIMER] <-- Fin OCR page: {pageImagePath} : {swOcr.ElapsedMilliseconds} ms");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[TIMER] !!! ERREUR OCR page {pageImagePath} apres {swOcr.ElapsedMilliseconds} ms: {ex.Message}");
                        pageResults.Add(new OcrResult { FullText = "", Confidence = 0, Words = new List<OcrWord>() });
                    }
                }

                ocrResult = OcrResult.Merge(pageResults);
            }
            else
            {
                var swOcr = System.Diagnostics.Stopwatch.StartNew();
                Console.WriteLine($"[TIMER] --> Debut OCR fichier unique: {filePath}");
                ocrResult = await _ocrService.ExtractTextAsync(filePath);
                Console.WriteLine($"[TIMER] <-- Fin OCR fichier unique: {swOcr.ElapsedMilliseconds} ms");
                ocrResult.PageTexts = new List<string> { ocrResult.FullText };
                ocrResult.PageCount = 1;
            }

            ocrResult.FullText = TextCleaner.RemoveInvisibleMarks(ocrResult.FullText);
            ocrResult.PageTexts = ocrResult.PageTexts.Select(TextCleaner.RemoveInvisibleMarks).ToList();

            foreach (var word in ocrResult.Words)
                word.Text = TextCleaner.RemoveInvisibleMarks(word.Text);
            foreach (var word in ocrResult.Words)
                word.Text = TextCleaner.RemoveStrayTableBorders(word.Text);

            Console.WriteLine($"[TIMER] ===== FIN {originalFileName}: {swTotal.ElapsedMilliseconds} ms TOTAL =====");
            return ocrResult;
        }

        public async Task<DocumentProcessingResult> ProcessFileAsync(string filePath, string originalFileName)
        {
            var ocrResult = await RunOcrOnlyAsync(filePath, originalFileName);

            var lines = _engine.GroupWordsIntoLines(ocrResult.Words);
            var documentType = _classifier.Classify(ocrResult.FullText);
            var parser = _parserFactory.GetParser(documentType);
            object? document = parser?.Parse(ocrResult.FullText, lines);

            bool needsReview = EvaluateNeedsReview(documentType, document, ocrResult.Confidence);

            return new DocumentProcessingResult
            {
                FileName = originalFileName,
                DetectedType = documentType.ToString(),
                PageCount = ocrResult.PageCount,
                OcrConfidence = ocrResult.Confidence,
                NeedsReview = needsReview,
                Document = document,
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