using Tesseract;

namespace Codeium_Security.OCR
{
    public class OcrResult
    {
        public string FullText { get; set; } = string.Empty;
        public float Confidence { get; set; }
        public List<OcrWord> Words { get; set; } = new();
        public int PageCount { get; set; } = 1;

        // Fusionne plusieurs résultats de pages en un seul document
        public static OcrResult Merge(List<OcrResult> pageResults)
        {
            var merged = new OcrResult();
            var textParts = new List<string>();
            var allWords = new List<OcrWord>();
            int verticalOffset = 0;

            foreach (var page in pageResults)
            {
                textParts.Add(page.FullText);

                // On décale les positions Y de chaque page pour ne pas mélanger
                // les lignes de pages différentes lors du regroupement en lignes
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
                verticalOffset += pageMaxBottom + 500; // +500 = marge de sécurité entre pages
            }

            merged.FullText = string.Join("\n\n", textParts);
            merged.Words = allWords;
            merged.Confidence = pageResults.Count > 0 ? pageResults.Average(p => p.Confidence) : 0;
            merged.PageCount = pageResults.Count;

            return merged;
        }
    }
}