using Codeium_Security.Models;
using Codeium_Security.Services;
using Microsoft.AspNetCore.Mvc;
using System.Text.Json;

namespace Codeium_Security.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class OcrController : ControllerBase
    {
        private readonly DocumentProcessingService _processingService;
        private static readonly string[] SupportedExtensions =
            { ".pdf", ".png", ".jpg", ".jpeg", ".tif", ".tiff", ".bmp", ".gif", ".webp" };

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true,
            PropertyNamingPolicy = null
        };

        public OcrController(DocumentProcessingService processingService)
        {
            _processingService = processingService;
        }

        [HttpPost]
        public async Task<IActionResult> Extract(List<IFormFile> files)
        {
            if (files == null || files.Count == 0)
                return BadRequest("No file uploaded.");

            var allResults = new List<DocumentProcessingResult>();

            foreach (var file in files)
            {
                if (file.Length == 0) continue;

                Directory.CreateDirectory("Images");
                var filePath = Path.Combine("Images", file.FileName);

                using (var stream = new FileStream(filePath, FileMode.Create))
                    await file.CopyToAsync(stream);

                var result = await _processingService.ProcessFileAsync(filePath, file.FileName);
                allResults.Add(result);
            }

            return Ok(allResults);
        }

        [HttpPost("process-batch")]
        public async Task<IActionResult> ProcessBatch()
        {
            const string imagesFolder = "Images";
            const string outputFolder = "TrainingData/RawResults";

            Directory.CreateDirectory(imagesFolder);
            Directory.CreateDirectory(outputFolder);

            var files = Directory.GetFiles(imagesFolder)
                .Where(f => SupportedExtensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
                .OrderBy(f => f)
                .ToList();

            if (files.Count == 0)
                return BadRequest($"No supported files found in {imagesFolder}/.");

            var savedFiles = new List<string>();
            var errors = new List<object>();

            foreach (var filePath in files)
            {
                var fileName = Path.GetFileName(filePath);
                var outputPath = Path.Combine(outputFolder, Path.GetFileNameWithoutExtension(fileName) + ".json");
                var errorPath = Path.Combine(outputFolder, Path.GetFileNameWithoutExtension(fileName) + ".error.txt");

                try
                {
                    if (System.IO.File.Exists(errorPath))
                        System.IO.File.Delete(errorPath);

                    var result = await _processingService.ProcessFileAsync(filePath, fileName);
                    var json = JsonSerializer.Serialize(result, JsonOptions);
                    await System.IO.File.WriteAllTextAsync(outputPath, json);
                    savedFiles.Add(outputPath);
                }
                catch (Exception ex)
                {
                    await System.IO.File.WriteAllTextAsync(errorPath, ex.ToString());
                    errors.Add(new { FileName = fileName, Error = ex.Message });
                }
            }

            return Ok(new
            {
                Count = savedFiles.Count,
                Files = savedFiles,
                Errors = errors
            });
        }
    }
}
