using System.Text.Json;
using Codeium_Security.Models;
using Codeium_Security.OCR;
using Codeium_Security.Services.DocumentMetadataExtraction;
using Xunit;
using Xunit.Abstractions;

namespace Codeium_Security.Tests
{
    // Test de diagnostic temporaire (harnais uniquement) : verifie CustomerName/ExtractionPeriod
    // sur les fichiers Attijari du dossier Desktop\Allfiles (120 PDF), pour confirmer le cas
    // signale par l'utilisateur ("Nom du client : BLUE TUNISIE" / "Periode du 01/11/2024 au
    // 30/11/2024"). N'appelle jamais BankDocumentParser : ne verifie/ne touche rien aux transactions.
    [Collection("Pipeline")]
    public class AllfilesAttijariDiagnosticTest
    {
        private readonly PipelineFixture _fixture;
        private readonly ITestOutputHelper _output;

        public AllfilesAttijariDiagnosticTest(PipelineFixture fixture, ITestOutputHelper output)
        {
            _fixture = fixture;
            _output = output;
        }

        private const string AllfilesDir = @"C:\Users\USER\OneDrive\Desktop\Allfiles";

        private static readonly string LogDir = Path.Combine(
            Environment.GetEnvironmentVariable("TEMP") ?? Path.GetTempPath(), "allfiles_diag");

        private class OcrWordDto
        {
            public string Text { get; set; } = "";
            public float Confidence { get; set; }
            public int Left { get; set; }
            public int Top { get; set; }
            public int Right { get; set; }
            public int Bottom { get; set; }
        }

        private class OcrCacheDto
        {
            public int PageCount { get; set; }
            public float Confidence { get; set; }
            public List<string> PageTexts { get; set; } = new();
            public List<OcrWordDto> Words { get; set; } = new();
        }

        private async Task<OcrResult> GetOcrCachedAsync(string pdfPath)
        {
            Directory.CreateDirectory(LogDir);
            string cachePath = Path.Combine(LogDir, "cache_" + SafeName(pdfPath) + ".json");

            if (File.Exists(cachePath))
            {
                var dto = JsonSerializer.Deserialize<OcrCacheDto>(File.ReadAllText(cachePath))!;
                return new OcrResult
                {
                    FullText = string.Join("\n\n", dto.PageTexts),
                    Confidence = dto.Confidence,
                    PageCount = dto.PageCount,
                    PageTexts = dto.PageTexts,
                    Words = dto.Words.Select(w => new OcrWord
                    {
                        Text = w.Text,
                        Confidence = w.Confidence,
                        Left = w.Left,
                        Top = w.Top,
                        Right = w.Right,
                        Bottom = w.Bottom
                    }).ToList()
                };
            }

            var ocr = await _fixture.ProcessingService.RunOcrOnlyAsync(pdfPath, Path.GetFileName(pdfPath));

            var toCache = new OcrCacheDto
            {
                PageCount = ocr.PageCount,
                Confidence = ocr.Confidence,
                PageTexts = ocr.PageTexts,
                Words = ocr.Words.Select(w => new OcrWordDto
                {
                    Text = w.Text,
                    Confidence = w.Confidence,
                    Left = w.Left,
                    Top = w.Top,
                    Right = w.Right,
                    Bottom = w.Bottom
                }).ToList()
            };
            File.WriteAllText(cachePath, JsonSerializer.Serialize(toCache));

            return ocr;
        }

        private static string SafeName(string pdfPath) =>
            Path.GetFileNameWithoutExtension(pdfPath).Replace(" ", "_");

        public static IEnumerable<object[]> AttijariFiles()
        {
            if (!Directory.Exists(AllfilesDir)) yield break;
            foreach (var f in Directory.EnumerateFiles(AllfilesDir, "*.pdf")
                         .Where(f => Path.GetFileName(f).Contains("attijari", StringComparison.OrdinalIgnoreCase))
                         .OrderBy(f => f))
                yield return new object[] { f };
        }

        public static IEnumerable<object[]> AllPdfFiles()
        {
            if (!Directory.Exists(AllfilesDir)) yield break;
            foreach (var f in Directory.EnumerateFiles(AllfilesDir, "*.pdf").OrderBy(f => f))
                yield return new object[] { f };
        }

        [Theory]
        [MemberData(nameof(AllPdfFiles))]
        public async Task DiagnoseMetadataAllBanks(string pdfPath)
        {
            var ocr = await GetOcrCachedAsync(pdfPath);
            var lines = _fixture.AnalysisEngine.GroupWordsIntoLines(ocr.Words);
            var rows = _fixture.AnalysisEngine.BuildTable(lines);

            var metadata = new GenericDocumentMetadataExtractor().Extract(ocr.FullText, rows);

            _output.WriteLine(
                $"[{Path.GetFileName(pdfPath)}] CustomerName='{metadata.CustomerName}' Period.Start='{metadata.Period?.Start}' Period.End='{metadata.Period?.End}'");
        }

        private static readonly string FauxDir = Path.Combine(AllfilesDir, "faux");

        public static IEnumerable<object[]> FauxFiles()
        {
            if (!Directory.Exists(FauxDir)) yield break;
            foreach (var f in Directory.EnumerateFiles(FauxDir, "*.pdf").OrderBy(f => f))
                yield return new object[] { f };
        }

        [Theory]
        [MemberData(nameof(FauxFiles))]
        public async Task DiagnoseMetadataFaux(string pdfPath)
        {
            var ocr = await GetOcrCachedAsync(pdfPath);
            var lines = _fixture.AnalysisEngine.GroupWordsIntoLines(ocr.Words);
            var rows = _fixture.AnalysisEngine.BuildTable(lines);

            var metadata = new GenericDocumentMetadataExtractor().Extract(ocr.FullText, rows);

            _output.WriteLine(
                $"[{Path.GetFileName(pdfPath)}] CustomerName='{metadata.CustomerName}' Period.Start='{metadata.Period?.Start}' Period.End='{metadata.Period?.End}'");
        }

        [Theory]
        [MemberData(nameof(FauxFiles))]
        public async Task DumpFullTextFaux(string pdfPath)
        {
            var ocr = await GetOcrCachedAsync(pdfPath);
            Console.WriteLine($"[FULLTEXT-START] {Path.GetFileName(pdfPath)}");
            Console.WriteLine(ocr.PageTexts.Count > 0 ? ocr.PageTexts[0] : ocr.FullText);
            Console.WriteLine($"[FULLTEXT-END] {Path.GetFileName(pdfPath)}");
        }

        public static IEnumerable<object[]> UibFiles()
        {
            if (!Directory.Exists(AllfilesDir)) yield break;
            foreach (var f in Directory.EnumerateFiles(AllfilesDir, "*.pdf")
                         .Where(f => Path.GetFileName(f).Contains("uib", StringComparison.OrdinalIgnoreCase))
                         .OrderBy(f => f))
                yield return new object[] { f };
        }

        [Theory]
        [MemberData(nameof(UibFiles))]
        public async Task DiagnoseUibBankName(string pdfPath)
        {
            var ocr = await GetOcrCachedAsync(pdfPath);
            var lines = _fixture.AnalysisEngine.GroupWordsIntoLines(ocr.Words);
            var rows = _fixture.AnalysisEngine.BuildTable(lines);
            var docType = _fixture.Classifier.Classify(ocr.FullText);
            var parser = _fixture.ParserFactory.GetParser(docType);
            var document = parser?.Parse(ocr.FullText, lines);
            string bankName = (document as BankDocument)?.BankName ?? "<non-BankDocument>";

            _output.WriteLine($"[{Path.GetFileName(pdfPath)}] BankName='{bankName}'");
        }

        [Theory]
        [MemberData(nameof(AttijariFiles))]
        public async Task DiagnoseMetadata(string pdfPath)
        {
            var ocr = await GetOcrCachedAsync(pdfPath);
            var lines = _fixture.AnalysisEngine.GroupWordsIntoLines(ocr.Words);
            var rows = _fixture.AnalysisEngine.BuildTable(lines);

            var metadata = new GenericDocumentMetadataExtractor().Extract(ocr.FullText, rows);
            var docType = _fixture.Classifier.Classify(ocr.FullText);
            var parser = _fixture.ParserFactory.GetParser(docType);
            var document = parser?.Parse(ocr.FullText, lines);
            string bankName = (document as BankDocument)?.BankName ?? "<non-BankDocument>";

            _output.WriteLine(
                $"[{Path.GetFileName(pdfPath)}] BankName='{bankName}' CustomerName='{metadata.CustomerName}' Period.Start='{metadata.Period?.Start}' Period.End='{metadata.Period?.End}'");
        }
    }
}
