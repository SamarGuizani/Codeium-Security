using Codeium_Security.OCR;
using Codeium_Security.Services;
using Codeium_Security.Services.DocumentParsers;
using Microsoft.AspNetCore.Mvc;

namespace Codeium_Security.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class OcrController : ControllerBase
    {
        private readonly IOcrService _ocrService;
        private readonly BankDocumentParser _parser;
        private readonly DocumentAnalysisEngine _engine;
        private readonly PdfToImageConverter _pdfConverter;

        public OcrController(
            IOcrService ocrService,
            BankDocumentParser parser,
            DocumentAnalysisEngine engine,
            PdfToImageConverter pdfConverter)
        {
            _ocrService = ocrService;
            _parser = parser;
            _engine = engine;
            _pdfConverter = pdfConverter;
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

                if (_pdfConverter.IsPdf(file.FileName))
                {
                    // PDF : convertir chaque page en image, OCR chaque page, fusionner
                    string pdfImagesFolder = Path.Combine("Images", "pdf_pages_" + Path.GetFileNameWithoutExtension(file.FileName));
                    var pageImagePaths = _pdfConverter.ConvertPdfToImages(filePath, pdfImagesFolder);

                    var pageResults = new List<OcrResult>();
                    foreach (var pageImagePath in pageImagePaths)
                    {
                        var pageResult = await _ocrService.ExtractTextAsync(pageImagePath);
                        pageResults.Add(pageResult);
                    }

                    ocrResult = OcrResult.Merge(pageResults);
                }
                else
                {
                    // Image simple (PNG, JPG...)
                    ocrResult = await _ocrService.ExtractTextAsync(filePath);
                }

                var lines = _engine.GroupWordsIntoLines(ocrResult.Words);
                var document = _parser.Parse(ocrResult.FullText, lines);

                allResults.Add(new
                {
                    FileName = file.FileName,
                    PageCount = ocrResult.PageCount,
                    Ocr = ocrResult,
                    Document = document
                });
            }

            return Ok(allResults);
        }
    }
}