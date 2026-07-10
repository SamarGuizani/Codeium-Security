namespace Codeium_Security.OCR
{
    public class OcrResult
    {

        public string FullText { get; set; } = string.Empty;

        public float Confidence { get; set; }

        public List<OcrWord> Words { get; set; } = new();
    }
}
