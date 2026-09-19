using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.AspNetCore.Components.Forms;
using Codeium_Security.Frontend.Models;

namespace Codeium_Security.Frontend.Services;

// Appelle les endpoints reels de Codeium_Security.Controllers.OcrController (backend ASP.NET Core).
public class OcrApiService
{
    private const long MaxUploadBytes = 30 * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;

    public OcrApiService(HttpClient http)
    {
        _http = http;
    }

    public async Task<ExcelFolderExportResult> ExportExcelFolderAsync(string inputFolder, string? outputFolder)
    {
        var query = $"api/Ocr/export-excel-folder?inputFolder={Uri.EscapeDataString(inputFolder)}";
        if (!string.IsNullOrWhiteSpace(outputFolder))
            query += $"&outputFolder={Uri.EscapeDataString(outputFolder)}";

        using var response = await _http.PostAsync(query, content: null);
        var body = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
            throw new ApiException(response.StatusCode, ExtractMessage(body));

        return JsonSerializer.Deserialize<ExcelFolderExportResult>(body, JsonOptions)
            ?? new ExcelFolderExportResult();
    }

    public async Task<string> ExtractTextAsync(IBrowserFile file)
    {
        using var content = BuildFileContent(file);
        using var response = await _http.PostAsync("api/Ocr/text", content);
        var body = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
            throw new ApiException(response.StatusCode, ExtractMessage(body));

        return body;
    }

    public async Task<(byte[] Bytes, string FileName)> ExportExcelAsync(IBrowserFile file)
    {
        using var content = BuildFileContent(file);
        using var response = await _http.PostAsync("api/Ocr/export-excel", content);

        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync();
            throw new ApiException(response.StatusCode, ExtractMessage(errorBody));
        }

        var bytes = await response.Content.ReadAsByteArrayAsync();
        var suggestedName = response.Content.Headers.ContentDisposition?.FileNameStar
            ?? response.Content.Headers.ContentDisposition?.FileName;
        var fileName = suggestedName?.Trim('"')
            ?? Path.GetFileNameWithoutExtension(file.Name) + ".xlsx";

        return (bytes, fileName);
    }

    private static MultipartFormDataContent BuildFileContent(IBrowserFile file)
    {
        var content = new MultipartFormDataContent();
        var streamContent = new StreamContent(file.OpenReadStream(MaxUploadBytes));
        streamContent.Headers.ContentType = new MediaTypeHeaderValue(
            string.IsNullOrEmpty(file.ContentType) ? "application/octet-stream" : file.ContentType);
        content.Add(streamContent, "file", file.Name);
        return content;
    }

    private static string ExtractMessage(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return "Le serveur n'a renvoye aucun detail.";

        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                doc.RootElement.TryGetProperty("title", out var title))
                return title.GetString() ?? body;
        }
        catch (JsonException)
        {
            // Le corps n'est pas du JSON (ex. BadRequest("texte brut")) : on le renvoie tel quel.
        }

        return body;
    }
}

public class ApiException : Exception
{
    public HttpStatusCode StatusCode { get; }

    public ApiException(HttpStatusCode statusCode, string message) : base(message)
    {
        StatusCode = statusCode;
    }
}
