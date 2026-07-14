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
            if (!_pdfConverter.IsPdf(originalFileName) && _formatConverter.NeedsConversion(originalFileName))
            {
                filePath = _formatConverter.ConvertToPng(filePath);
            }

            OcrResult ocrResult;

            if (_pdfConverter.IsPdf(originalFileName))
            {
                string pdfImagesFolder = Path.Combine("Images", "pdf_pages_" + Path.GetFileNameWithoutExtension(originalFileName));
                var pageImagePaths = _pdfConverter.ConvertPdfToImages(filePath, pdfImagesFolder);

                var pageResults = new List<OcrResult>();
                foreach (var pageImagePath in pageImagePaths)
                    pageResults.Add(await _ocrService.ExtractTextAsync(pageImagePath));

                ocrResult = OcrResult.Merge(pageResults);
            }
            else
            {
                ocrResult = await _ocrService.ExtractTextAsync(filePath);
            }

            ocrResult.FullText = TextCleaner.RemoveInvisibleMarks(ocrResult.FullText);
            foreach (var word in ocrResult.Words)
                word.Text = TextCleaner.RemoveInvisibleMarks(word.Text);

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
                Document = document
            };
        }

        public string FormatAsPlainText(string fileName, int pageCount, string fullText)
        {
            var sb = new System.Text.StringBuilder();

            if (pageCount <= 1)
            {
                sb.AppendLine($"****** Résultat pour {fileName} ******");
                sb.AppendLine(fullText.Trim());
            }
            else
            {
                var pages = fullText.Split(new[] { "\n\n" }, StringSplitOptions.None);
                for (int i = 0; i < pages.Length; i++)
                {
                    sb.AppendLine($"****** Résultat pour {fileName} - Page {i + 1} ******");
                    sb.AppendLine(pages[i].Trim());
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
                if (string.IsNullOrWhiteSpace(bank.AccountNumber))
                    return true;
                if (bank.Balance == 0 && bank.Transactions.Count == 0)
                    return true;
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