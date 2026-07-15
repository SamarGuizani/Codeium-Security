namespace Codeium_Security.Models
{
    public class DocumentProcessingResult
    {
        public string FileName { get; set; } = "";
        public string DetectedType { get; set; } = "";
        public int PageCount { get; set; }
        public float OcrConfidence { get; set; }
        public bool NeedsReview { get; set; }
        public string? RawOcrText { get; set; }
        public object? Document { get; set; }
        public List<string> DebugLines { get; set; } = new();

        public List<WordCoordinate> Words { get; set; } = new();

        public List<LineOutput> Lines { get; set; } = new();
    }
}
