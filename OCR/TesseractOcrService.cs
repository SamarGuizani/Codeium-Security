using System.Threading.Tasks;
using Tesseract;

namespace Codeium_Security.OCR
{
    public class TesseractOcrService : IOcrService
    {
        public async Task<OcrResult> ExtractTextAsync(string imagePath)
        {
            return await Task.Run(() =>
            {
                using var engine = new TesseractEngine(
                    "./tessdata",
                    "eng+fra+ara",
                    EngineMode.Default);

                using var img = Pix.LoadFromFile(imagePath);

                using var page = engine.Process(img);

                var result = new OcrResult
                {
                    FullText = page.GetText(),
                    Confidence = page.GetMeanConfidence() * 100
                };

                return result;
            });
        }
    }
}