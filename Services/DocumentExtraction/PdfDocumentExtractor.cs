using Codeium_Security.Interfaces;
using Codeium_Security.OCR;

namespace Codeium_Security.Services.DocumentExtraction
{
    // Logique inchangee : deplacee telle quelle depuis DocumentProcessingService.RunOcrOnlyAsync
    // (branche PDF). Aucun comportement modifie.
    public class PdfDocumentExtractor : IDocumentExtractor
    {
        private readonly IOcrService _ocrService;
        private readonly PdfToImageConverter _pdfConverter;

        public PdfDocumentExtractor(IOcrService ocrService, PdfToImageConverter pdfConverter)
        {
            _ocrService = ocrService;
            _pdfConverter = pdfConverter;
        }

        public bool CanHandle(string fileName) => _pdfConverter.IsPdf(fileName);

        public async Task<OcrResult> ExtractAsync(string filePath, string originalFileName)
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

            return OcrResult.Merge(pageResults);
        }
    }
}
