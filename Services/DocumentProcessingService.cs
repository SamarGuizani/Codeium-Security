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

        public async Task<DocumentProcessingResult> ProcessFileAsync(string filePath, string displayFileName)
        {
            var workingPath = filePath;

            if (!_pdfConverter.IsPdf(displayFileName) && _formatConverter.NeedsConversion(displayFileName))
                workingPath = _formatConverter.ConvertToPng(filePath);

            OcrResult ocrResult;

            if (_pdfConverter.IsPdf(displayFileName))
            {
                string pdfImagesFolder = Path.Combine(
                    Path.GetDirectoryName(filePath) ?? "Images",
                    "pdf_pages_" + Path.GetFileNameWithoutExtension(displayFileName));

                var pageImagePaths = _pdfConverter.ConvertPdfToImages(workingPath, pdfImagesFolder);
                var pageResults = new List<OcrResult>();

                foreach (var pageImagePath in pageImagePaths)
                    pageResults.Add(await _ocrService.ExtractTextAsync(pageImagePath));

                ocrResult = OcrResult.Merge(pageResults);
            }
            else
            {
                ocrResult = await _ocrService.ExtractTextAsync(workingPath);
            }

            ocrResult.FullText = TextCleaner.RemoveInvisibleMarks(ocrResult.FullText);
            foreach (var word in ocrResult.Words)
                word.Text = TextCleaner.RemoveInvisibleMarks(word.Text);

            var lines = _engine.GroupWordsIntoLines(ocrResult.Words);
            var documentType = _classifier.Classify(ocrResult.FullText);
            var parser = _parserFactory.GetParser(documentType);
            object? document = parser?.Parse(ocrResult.FullText, lines);

            bool includeRawOcr = _configuration.GetValue("Ocr:IncludeRawText", true);
            int rawTextMaxLength = _configuration.GetValue("Ocr:RawTextMaxLength", 500);
            float reviewThreshold = _configuration.GetValue("Ocr:ReviewConfidenceThreshold", 85f);

            return new DocumentProcessingResult
            {
                FileName = displayFileName,
                DetectedType = documentType.ToString(),
                PageCount = ocrResult.PageCount,
                OcrConfidence = ocrResult.Confidence,
                NeedsReview = EvaluateNeedsReview(documentType, document, ocrResult.Confidence, reviewThreshold),
                RawOcrText = includeRawOcr
                    ? TruncateText(ocrResult.FullText, rawTextMaxLength)
                    : null,
                Document = document,
                DebugLines = lines.Select(l => l.FullLineText).ToList()
            };
        }

        private static bool EvaluateNeedsReview(
            DocumentType documentType,
            object? document,
            float confidence,
            float reviewThreshold)
        {
            if (confidence < reviewThreshold)
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
