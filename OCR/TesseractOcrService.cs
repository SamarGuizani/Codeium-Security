using System.Threading.Tasks;
using Tesseract;

namespace Codeium_Security.OCR
{
    public class TesseractOcrService : IOcrService
    {
        private readonly TesseractEngine _engine;
        private readonly object _lock = new(); // TesseractEngine n'est pas thread-safe pour des appels concurrents

        public TesseractOcrService(IConfiguration configuration)
        {
            var languages = configuration.GetValue("Ocr:Languages", "eng+fra+ara")!;
            var pageSegMode = configuration.GetValue("Ocr:PageSegMode", 6);

            // Cree le moteur UNE SEULE FOIS, au demarrage du service (pas a chaque page)
            _engine = new TesseractEngine("./tessdata", languages, EngineMode.Default);
            _engine.SetVariable("tessedit_pageseg_mode", pageSegMode.ToString());
            _engine.SetVariable("user_defined_dpi", "300");
        }

        public async Task<OcrResult> ExtractTextAsync(string imagePath)
        {
            return await Task.Run(() =>
            {
                lock (_lock)
                {
                    using var img = Pix.LoadFromFile(imagePath);
                    using var page = _engine.Process(img);

                    var text = page.GetText();
                    Console.WriteLine("====================================");
                    Console.WriteLine("TEXTE BRUT TESSERACT");
                    Console.WriteLine(text);
                    Console.WriteLine("====================================");
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
                }
            });
        }
    }
}