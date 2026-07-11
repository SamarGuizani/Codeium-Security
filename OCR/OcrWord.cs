namespace Codeium_Security.OCR
{
    public class OcrWord
    {
        public string Text { get; set; } = "";
        public float Confidence { get; set; }
        public int Left { get; set; }
        public int Top { get; set; }
        public int Right { get; set; }
        public int Bottom { get; set; }
    }
}