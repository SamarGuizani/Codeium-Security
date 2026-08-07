using Tesseract;

namespace Codeium_Security.OCR
{
    public class OcrResult
    {
        public string FullText { get; set; } = string.Empty;
        public float Confidence { get; set; }
        public List<OcrWord> Words { get; set; } = new();
        public int PageCount { get; set; } = 1;
        public List<string> PageTexts { get; set; } = new();
        public int? SuggestedVerticalTolerance { get; set; }

        public static OcrResult Merge(List<OcrResult> pageResults)
        {
            var merged = new OcrResult();
            var textParts = new List<string>();
            var allWords = new List<OcrWord>();
            var pageTexts = new List<string>();
            int verticalOffset = 0;

            foreach (var page in pageResults)
            {
                textParts.Add(page.FullText);
                pageTexts.Add(page.FullText);

                foreach (var word in page.Words)
                {
                    allWords.Add(new OcrWord
                    {
                        Text = word.Text,
                        Confidence = word.Confidence,
                        Left = word.Left,
                        Top = word.Top + verticalOffset,
                        Right = word.Right,
                        Bottom = word.Bottom + verticalOffset
                    });
                }

                int pageMaxBottom = page.Words.Count > 0 ? page.Words.Max(w => w.Bottom) : 0;
                verticalOffset += pageMaxBottom + 500;
            }

            merged.FullText = string.Join("\n\n", textParts);
            merged.Words = allWords;
            merged.Confidence = pageResults.Count > 0 ? pageResults.Average(p => p.Confidence) : 0;
            merged.PageCount = pageResults.Count;
            merged.PageTexts = pageTexts;

            return merged;
        }
    }
}