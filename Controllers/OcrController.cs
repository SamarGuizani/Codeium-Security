using Codeium_Security.Factories;
using Codeium_Security.OCR;
using Codeium_Security.Services;
using Codeium_Security.Services.DocumentClassification;
using Codeium_Security.Utilities;
using Microsoft.AspNetCore.Mvc;

namespace Codeium_Security.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class OcrController : ControllerBase
    {
        private readonly IOcrService _ocrService;
        private readonly DocumentAnalysisEngine _engine;
        private readonly PdfToImageConverter _pdfConverter;
        private readonly IDocumentClassifier _classifier;
        private readonly DocumentParserFactory _parserFactory;
        private readonly ImageFormatConverter _formatConverter;

        public OcrController(
            IOcrService ocrService,
            DocumentAnalysisEngine engine,
            PdfToImageConverter pdfConverter,
            IDocumentClassifier classifier,
            DocumentParserFactory parserFactory,
            ImageFormatConverter formatConverter)
        {
            _ocrService = ocrService;
            _engine = engine;
            _pdfConverter = pdfConverter;
            _classifier = classifier;
            _parserFactory = parserFactory;
            _formatConverter = formatConverter;
        }

        [HttpPost]
        public async Task<IActionResult> Extract(List<IFormFile> files)
        {
            if (files == null || files.Count == 0)
                return BadRequest("No file uploaded.");

            var allResults = new List<object>();

            foreach (var file in files)
            {
                if (file.Length == 0) continue;

                Directory.CreateDirectory("Images");
                var filePath = Path.Combine("Images", file.FileName);

                using (var stream = new FileStream(filePath, FileMode.Create))
                {
                    await file.CopyToAsync(stream);
                }

                OcrResult ocrResult;

                // Si le format n'est pas supporté par Tesseract (webp, etc.), on convertit d'abord
                if (!_pdfConverter.IsPdf(file.FileName) && _formatConverter.NeedsConversion(file.FileName))
                {
                    filePath = _formatConverter.ConvertToPng(filePath);
                }

                if (_pdfConverter.IsPdf(file.FileName))
                {
                    string pdfImagesFolder = Path.Combine("Images", "pdf_pages_" + Path.GetFileNameWithoutExtension(file.FileName));
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
              

                // Nettoyage des caractères invisibles AVANT tout traitement
                ocrResult.FullText = TextCleaner.RemoveInvisibleMarks(ocrResult.FullText);
                foreach (var word in ocrResult.Words)
                {
                    word.Text = TextCleaner.RemoveInvisibleMarks(word.Text);
                }
                // ── C'est ici que le diagramme prend vie ──
                var lines = _engine.GroupWordsIntoLines(ocrResult.Words);
                var documentType = _classifier.Classify(ocrResult.FullText);
                var parser = _parserFactory.GetParser(documentType);
                object? document = parser?.Parse(ocrResult.FullText, lines);

                allResults.Add(new
                {
                    FileName = file.FileName,
                    DetectedType = documentType.ToString(),
                    PageCount = ocrResult.PageCount,
                    Document = document,
                    DebugLines = lines.Select(l => l.FullLineText).ToList()   // ← TEMPORAIRE, à retirer après debug
                });
            }

            return Ok(allResults);
        }
    }
}