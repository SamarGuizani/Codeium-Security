using Codeium_Security.Models;
using Codeium_Security.OCR;
using Codeium_Security.Services;
using Microsoft.AspNetCore.Mvc;
using System.Collections.Generic;
using System.IO.Compression;
using System.Text.Json;
using System.Text.RegularExpressions;


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

        // Ecrit un ou plusieurs fichiers JSON pour un resultat donne :
        // - un fichier par sous-compte si result.Document est un BankDocument avec des Accounts
        // - sinon, comportement inchange (un seul fichier)
        // Retourne la liste des chemins ecrits.
        private async Task<List<string>> SaveResultAsJson(DocumentProcessingResult result, string fileName, string outputFolder)
        {
            var written = new List<string>();

            if (result.Document is BankDocument bankDoc && bankDoc.Accounts.Count > 0)
            {
                foreach (var account in bankDoc.Accounts)
                {
                    string safeAccount = string.IsNullOrWhiteSpace(account.AccountNumber)
                        ? "compte_inconnu"
                        : Regex.Replace(account.AccountNumber, @"[^\w\-]", "_");

                    var perAccountResult = new
                    {
                        FileName = fileName,
                        result.DetectedType,
                        result.PageCount,
                        result.OcrConfidence,
                        result.NeedsReview,
                        BankName = bankDoc.BankName,
                        Account = account
                    };

                    var outputPath = Path.Combine(outputFolder,
                        $"{Path.GetFileNameWithoutExtension(fileName)}_{safeAccount}.json");
                    var json = JsonSerializer.Serialize(perAccountResult, JsonOptions);
                    await System.IO.File.WriteAllTextAsync(outputPath, json);
                    written.Add(outputPath);
                }
            }
            else
            {
                var outputPath = Path.Combine(outputFolder, Path.GetFileNameWithoutExtension(fileName) + ".json");
                var json = JsonSerializer.Serialize(result, JsonOptions);
                await System.IO.File.WriteAllTextAsync(outputPath, json);
                written.Add(outputPath);
            }

            return written;
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

                await SaveResultAsJson(result, file.FileName, "TrainingData/RawResults");
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
        [HttpPost("debug-html")]
        public async Task<IActionResult> DebugHtml(IFormFile file)
        {
            string tempPath = Path.Combine(Path.GetTempPath(), file.FileName);
            using (var stream = new FileStream(tempPath, FileMode.Create))
            {
                await file.CopyToAsync(stream);
            }

            var configBuilder = new ConfigurationBuilder()
                .SetBasePath(Directory.GetCurrentDirectory())
                .AddJsonFile("appsettings.json", optional: true);
            var config = configBuilder.Build();

            var ocrService = new TesseractOcrService(config);
            var pdfConverter = new PdfToImageConverter(config);

            string pagesFolder = Path.Combine("Images", "debug_" + Path.GetFileNameWithoutExtension(file.FileName));
            var pageImagePaths = pdfConverter.ConvertPdfToImages(tempPath, pagesFolder);

            var engine = new DocumentAnalysisEngine();
            var html = new System.Text.StringBuilder();
            html.Append("<html><head><meta charset='utf-8'><style>");
            html.Append("body{font-family:monospace;background:#111;color:#eee;} ");
            html.Append("table{border-collapse:collapse;margin-bottom:30px;width:100%;} ");
            html.Append("td{border:1px solid #444;padding:4px 8px;font-size:12px;white-space:nowrap;} ");
            html.Append("h2{color:#4ea;}");
            html.Append("</style></head><body>");

            int pageNum = 1;
            foreach (var pageImagePath in pageImagePaths)
            {
                var ocrResult = await ocrService.ExtractTextAsync(pageImagePath);
                var lines = engine.GroupWordsIntoLines(ocrResult.Words);
                var rows = engine.BuildTable(lines);

                html.Append($"<h2>Page {pageNum}</h2><table>");
                foreach (var row in rows)
                {
                    html.Append("<tr>");
                    foreach (var cell in row.Cells)
                    {
                        html.Append($"<td title='Left={cell.Left}'>{System.Net.WebUtility.HtmlEncode(cell.Text)}</td>");
                    }
                    html.Append("</tr>");
                }
                html.Append("</table>");
                pageNum++;
            }

            html.Append("</body></html>");

            string outputHtmlPath = Path.Combine("TrainingData", "RawResults", Path.GetFileNameWithoutExtension(file.FileName) + "_debug.html");
            await System.IO.File.WriteAllTextAsync(outputHtmlPath, html.ToString());

            return PhysicalFile(Path.GetFullPath(outputHtmlPath), "text/html");
        }

        [HttpPost("json-to-html")]
        public async Task<IActionResult> JsonToHtml(IFormFile file)
        {
            using var reader = new StreamReader(file.OpenReadStream());
            string jsonContent = await reader.ReadToEndAsync();

            using var doc = System.Text.Json.JsonDocument.Parse(jsonContent);
            var root = doc.RootElement;

            var html = new System.Text.StringBuilder();
            html.Append("<html><head><meta charset='utf-8'><style>");
            html.Append("body{font-family:Arial,sans-serif;background:#111;color:#eee;padding:20px;} ");
            html.Append("table{border-collapse:collapse;width:100%;margin-bottom:20px;} ");
            html.Append("td,th{border:1px solid #444;padding:6px 10px;font-size:13px;text-align:left;} ");
            html.Append("th{background:#2a2a2a;} ");
            html.Append("tr:nth-child(even){background:#1a1a1a;} ");
            html.Append(".neg{color:#ff6b6b;} .pos{color:#6bff8f;} ");
            html.Append("h2{color:#4ea;} h3{color:#8cf;}");
            html.Append("</style></head><body>");

            html.Append($"<h2>{System.Net.WebUtility.HtmlEncode(file.FileName)}</h2>");

            if (root.TryGetProperty("BankName", out var bankName))
                html.Append($"<p><b>Banque:</b> {System.Net.WebUtility.HtmlEncode(bankName.GetString())}</p>");

            var accountList = new List<System.Text.Json.JsonElement>();

            if (root.TryGetProperty("Accounts", out var accountsArr) && accountsArr.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                foreach (var a in accountsArr.EnumerateArray())
                    accountList.Add(a);
            }
            else if (root.TryGetProperty("Account", out var singleAccount) && singleAccount.ValueKind == System.Text.Json.JsonValueKind.Object)
            {
                accountList.Add(singleAccount);
            }
            else
            {
                html.Append("<p style='color:red'>Aucune propriete 'Account' ou 'Accounts' trouvee dans ce JSON.</p>");
            }

            foreach (var account in accountList)
            {
                html.Append("<h3>Compte : ");
                if (account.TryGetProperty("AccountNumber", out var acc))
                    html.Append(System.Net.WebUtility.HtmlEncode(acc.GetString()));
                html.Append("</h3>");

                html.Append("<p>");
                if (account.TryGetProperty("SoldeInitial", out var si)) html.Append($"Solde Initial: {si} &nbsp;&nbsp; ");
                if (account.TryGetProperty("SoldeFinal", out var sf)) html.Append($"Solde Final: {sf} &nbsp;&nbsp; ");
                if (account.TryGetProperty("TotalDebit", out var td)) html.Append($"Total Debit: {td} &nbsp;&nbsp; ");
                if (account.TryGetProperty("TotalCredit", out var tc)) html.Append($"Total Credit: {tc}");
                html.Append("</p>");

                html.Append("<table><tr><th>#</th><th>Date</th><th>Libelle</th><th>Debit</th><th>Credit</th><th>Solde</th></tr>");

                if (account.TryGetProperty("Transactions", out var txs) && txs.ValueKind == System.Text.Json.JsonValueKind.Array)
                {
                    int i = 1;
                    foreach (var tx in txs.EnumerateArray())
                    {
                        string date = tx.TryGetProperty("Date", out var d) ? d.GetString() ?? "" : "";
                        string libelle = tx.TryGetProperty("Libelle", out var l) ? l.GetString() ?? "" : "";
                        string debit = tx.TryGetProperty("Debit", out var deb) && deb.ValueKind != System.Text.Json.JsonValueKind.Null ? deb.ToString() : "";
                        string credit = tx.TryGetProperty("Credit", out var cred) && cred.ValueKind != System.Text.Json.JsonValueKind.Null ? cred.ToString() : "";
                        string solde = tx.TryGetProperty("Solde", out var sol) && sol.ValueKind != System.Text.Json.JsonValueKind.Null ? sol.ToString() : "";

                        html.Append($"<tr><td>{i}</td><td>{System.Net.WebUtility.HtmlEncode(date)}</td><td>{System.Net.WebUtility.HtmlEncode(libelle)}</td>");
                        html.Append($"<td class='neg'>{debit}</td><td class='pos'>{credit}</td><td>{solde}</td></tr>");
                        i++;
                    }
                }
                html.Append("</table>");
            }

            html.Append("</body></html>");

            string outputPath = Path.Combine("TrainingData", "RawResults", Path.GetFileNameWithoutExtension(file.FileName) + "_view.html");
            await System.IO.File.WriteAllTextAsync(outputPath, html.ToString());

            return PhysicalFile(Path.GetFullPath(outputPath), "text/html");
        }
        [HttpPost("process-batch")]
        public async Task<IActionResult> ProcessBatch(List<IFormFile>? files)
        {
            const string imagesFolder = "TrainingData/SourceDocuments";
            const string outputFolder = "TrainingData/RawResults";

            Directory.CreateDirectory(imagesFolder);
            Directory.CreateDirectory(outputFolder);

            if (files != null && files.Count > 0)
            {
                foreach (var file in files)
                {
                    if (file.Length == 0) continue;

                    if (Path.GetExtension(file.FileName).Equals(".zip", StringComparison.OrdinalIgnoreCase))
                    {
                        using var zipStream = file.OpenReadStream();
                        using var archive = new System.IO.Compression.ZipArchive(zipStream, System.IO.Compression.ZipArchiveMode.Read);

                        foreach (var entry in archive.Entries)
                        {
                            if (string.IsNullOrEmpty(entry.Name)) continue;

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
                var errorPath = Path.Combine(outputFolder, Path.GetFileNameWithoutExtension(fileName) + ".error.txt");

                try
                {
                    if (System.IO.File.Exists(errorPath))
                        System.IO.File.Delete(errorPath);

                    var result = await _processingService.ProcessFileAsync(filePath, fileName);
                    var written = await SaveResultAsJson(result, fileName, outputFolder);
                    savedFiles.AddRange(written);
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