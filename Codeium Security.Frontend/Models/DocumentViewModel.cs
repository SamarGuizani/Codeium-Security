namespace Codeium_Security.Frontend.Models;

public class DocumentViewModel
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string FileName { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
    public string FileType { get; set; } = string.Empty;
    public string? Banque { get; set; }
    public DateTime UploadedAt { get; set; } = DateTime.Now;
    public ProcessingStatus Status { get; set; } = ProcessingStatus.EnAttente;

    public string SizeDisplay => SizeBytes < 1024 * 1024
        ? $"{SizeBytes / 1024.0:0.#} Ko"
        : $"{SizeBytes / (1024.0 * 1024.0):0.##} Mo";
}
