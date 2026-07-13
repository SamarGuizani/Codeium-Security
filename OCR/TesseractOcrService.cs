using System.Threading.Tasks;
using Tesseract;

namespace Codeium_Security.OCR
{
    public class TesseractOcrService : IOcrService
    {
        private readonly IConfiguration _configuration;

        public TesseractOcrService(IConfiguration configuration)
        {
            _configuration = configuration;
        }

        public async Task<OcrResult> ExtractTextAsync(string imagePath)
        {
            return await Task.Run(() =>
            {
                var languages = _configuration.GetValue("Ocr:Languages", "eng+fra+ara")!;
                var pageSegMode = _configuration.GetValue("Ocr:PageSegMode", 6);

                using var engine = new TesseractEngine(
                    "./tessdata",
                    languages,
                    EngineMode.Default);

                engine.SetVariable("tessedit_pageseg_mode", pageSegMode.ToString());
                engine.SetVariable("user_defined_dpi", "300");

                using var img = Pix.LoadFromFile(imagePath);
                using var page = engine.Process(img);

                var text = page.GetText();
                var confidence = page.GetMeanConfidence();
                var words = new List<OcrWord>();

                using (var iter = page.GetIterator())
                {
                    iter.Begin();
                    do
                    {
                        if (iter.TryGetBoundingBox(PageIteratorLevel.Word, out Rect bounds))
                        {
                            string wordText = iter.GetText(PageIteratorLevel.Word);
                            float wordConfidence = iter.GetConfidence(PageIteratorLevel.Word);

                            if (!string.IsNullOrWhiteSpace(wordText))
                            {
                                words.Add(new OcrWord
                                {
                                    Text = wordText.Trim(),
                                    Confidence = wordConfidence,
                                    Left = bounds.X1,
                                    Top = bounds.Y1,
                                    Right = bounds.X2,
                                    Bottom = bounds.Y2
                                });
                            }
                        }
                    } while (iter.Next(PageIteratorLevel.Word));
                }

                return new OcrResult
                {
                    FullText = text,
                    Confidence = confidence * 100,
                    Words = words
                };
            });
        }
    }
}
