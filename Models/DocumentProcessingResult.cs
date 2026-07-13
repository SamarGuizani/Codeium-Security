namespace Codeium_Security.Models
{
    public class DocumentProcessingResult
    {
        public string FileName { get; set; } = string.Empty;
        public string DetectedType { get; set; } = string.Empty;
        public int PageCount { get; set; }
        public float OcrConfidence { get; set; }
        public bool NeedsReview { get; set; }
        public string? RawOcrText { get; set; }
        public object? Document { get; set; }
        public List<string> DebugLines { get; set; } = new();
    }
}
