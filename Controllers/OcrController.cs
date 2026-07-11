using Codeium_Security.OCR;
using Codeium_Security.Services;
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

        public OcrController(IOcrService ocrService, BankDocumentParser parser, DocumentAnalysisEngine engine)
        {
            _ocrService = ocrService;
            _parser = parser;
            _engine = engine;
        }

        [HttpPost]
        public async Task<IActionResult> Extract(IFormFile file)
        {
            if (file == null || file.Length == 0)
                return BadRequest("No file uploaded.");

            Directory.CreateDirectory("Images");

            var filePath = Path.Combine("Images", file.FileName);

            using (var stream = new FileStream(filePath, FileMode.Create))
            {
                await file.CopyToAsync(stream);
            }

            var result = await _ocrService.ExtractTextAsync(filePath);

            var lines = _engine.GroupWordsIntoLines(result.Words);

            var document = _parser.Parse(result.FullText, lines);

            return Ok(new
            {
                Ocr = result,
                Document = document
            });
        }
    }
}