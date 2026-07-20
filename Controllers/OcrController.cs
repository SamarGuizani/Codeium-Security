using Codeium_Security.Models;
using Codeium_Security.Services;
using Microsoft.AspNetCore.Mvc;
using System.Text.Json;
using System.IO.Compression;

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
            PropertyNamingPolicy = null,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
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

            Directory.CreateDirectory("TrainingData/RawResults");
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

                // Sauvegarde automatique : chaque test est désormais conservé sur le disque
                var outputPath = Path.Combine("TrainingData/RawResults",
                    Path.GetFileNameWithoutExtension(file.FileName) + ".json");
                var json = JsonSerializer.Serialize(result, JsonOptions);
                await System.IO.File.WriteAllTextAsync(outputPath, json);
            }

            return Ok(allResults);
        }

        [HttpPost("text")]
        public async Task<IActionResult> ExtractPlainText(IFormFile file)
        {
            if (file == null || file.Length == 0)
                return BadRequest("No file uploaded.");

            Directory.CreateDirectory("Images");
            var filePath = Path.Combine("Images", file.FileName);

            using (var stream = new FileStream(filePath, FileMode.Create))
            {
                await file.CopyToAsync(stream);
            }

            var ocrResult = await _processingService.RunOcrOnlyAsync(filePath, file.FileName);
            string plainText = _processingService.FormatAsPlainText(file.FileName, ocrResult.PageTexts);

            return Content(plainText, "text/plain; charset=utf-8");
        }
        [HttpGet("compare")]
        public IActionResult Compare()
        {
            var writer = new StringWriter();
            var originalOut = Console.Out;
            Console.SetOut(writer);

            CompareResults.Run();

            Console.SetOut(originalOut);
            return Content(writer.ToString(), "text/plain; charset=utf-8");
        }

        [HttpPost("process-batch")]
        public async Task<IActionResult> ProcessBatch(List<IFormFile>? files)
        {
            const string imagesFolder = "TrainingData/SourceDocuments";
            const string outputFolder = "TrainingData/RawResults";

            Directory.CreateDirectory(imagesFolder);
            Directory.CreateDirectory(outputFolder);

            // Si des fichiers sont uploadés (directement ou via un .zip), on les ajoute au dossier Images/
            if (files != null && files.Count > 0)
            {
                foreach (var file in files)
                {
                    if (file.Length == 0) continue;

                    if (Path.GetExtension(file.FileName).Equals(".zip", StringComparison.OrdinalIgnoreCase))
                    {
                        // Extraction du zip directement dans Images/
                        using var zipStream = file.OpenReadStream();
                        using var archive = new System.IO.Compression.ZipArchive(zipStream, System.IO.Compression.ZipArchiveMode.Read);

                        foreach (var entry in archive.Entries)
                        {
                            if (string.IsNullOrEmpty(entry.Name)) continue; // ignore les dossiers

                            string extractPath = Path.Combine(imagesFolder, entry.Name);
                            using var entryStream = entry.Open();
                            using var outputStream = new FileStream(extractPath, FileMode.Create);
                            await entryStream.CopyToAsync(outputStream);
                        }
                    }
                    else
                    {
                        string filePath = Path.Combine(imagesFolder, file.FileName);
                        using var stream = new FileStream(filePath, FileMode.Create);
                        await file.CopyToAsync(stream);
                    }
                }
            }

            var supportedExtensions = new[] { ".pdf", ".png", ".jpg", ".jpeg", ".tif", ".tiff", ".bmp", ".gif", ".webp" };

            var filesToProcess = Directory.GetFiles(imagesFolder)
                .Where(f => supportedExtensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
                .OrderBy(f => f)
                .ToList();

            if (filesToProcess.Count == 0)
                return BadRequest($"Aucun fichier supporté trouvé dans {imagesFolder}/.");

            var savedFiles = new List<string>();
            var errors = new List<object>();

            foreach (var filePath in filesToProcess)
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