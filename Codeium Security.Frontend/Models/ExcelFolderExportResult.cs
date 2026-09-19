namespace Codeium_Security.Frontend.Models;

// Miroir de la reponse JSON de POST /api/Ocr/export-excel-folder (backend OcrController).
public class ExcelFolderExportResult
{
    public string InputFolder { get; set; } = "";
    public string OutputFolder { get; set; } = "";
    public int Count { get; set; }
    public List<string> Files { get; set; } = new();
    public List<ExcelExportError> Errors { get; set; } = new();
}

public class ExcelExportError
{
    public string FileName { get; set; } = "";
    public string Error { get; set; } = "";
}
