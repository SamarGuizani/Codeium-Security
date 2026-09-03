using System.Reflection;
using System.Text.Json;
using Codeium_Security.Models;
using Codeium_Security.OCR;
using Codeium_Security.Services.DocumentMetadataExtraction;
using Xunit;
using Xunit.Abstractions;

namespace Codeium_Security.Tests
{
    // Diagnostic temporaire (harnais uniquement) pour investiguer pourquoi ExtractionPeriod
    // (voir GenericDocumentMetadataExtractor.ExtractPeriod) reste vide sur la quasi-totalite
    // des releves reels du dossier Allfiles. Met en cache le resultat OCR sur disque pour
    // iterer rapidement sur le regex seul, sans refaire l'OCR Tesseract a chaque execution.
    [Collection("Pipeline")]
    public class PeriodDiagnosticTest
    {
        private readonly PipelineFixture _fixture;
        private readonly ITestOutputHelper _output;

        public PeriodDiagnosticTest(PipelineFixture fixture, ITestOutputHelper output)
        {
            _fixture = fixture;
            _output = output;
        }

        private static readonly string LogDir = Path.Combine(
            Environment.GetEnvironmentVariable("TEMP") ?? Path.GetTempPath(), "period_diag");

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
            string cachePath = Path.Combine(LogDir, "cache_" + Path.GetFileNameWithoutExtension(pdfPath).Replace(" ", "_") + ".json");

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

        [Theory]
        [InlineData(@"C:\Users\USER\OneDrive\Desktop\Allfiles\BIAT -02.pdf")]
        [InlineData(@"C:\Users\USER\OneDrive\Desktop\Allfiles\BTL 02.pdf")]
        [InlineData(@"C:\Users\USER\OneDrive\Desktop\Allfiles\QNB MED 01-24.pdf")]
        [InlineData(@"C:\Users\USER\OneDrive\Desktop\Allfiles\Relevé Zitouna 06.pdf")]
        [InlineData(@"C:\Users\USER\OneDrive\Desktop\Allfiles\EXTRAIT ATB.pdf")]
        [InlineData(@"C:\Users\USER\OneDrive\Desktop\Allfiles\AMEN BQList.pdf")]
        [InlineData(@"C:\Users\USER\OneDrive\Desktop\Allfiles\BANK-TND.pdf")]
        [InlineData(@"C:\Users\USER\OneDrive\Desktop\Allfiles\BNA 7-2025.pdf")]
        [InlineData(@"C:\Users\USER\OneDrive\Desktop\Allfiles\Relevé_de_Compte_07-2024 AL BARAKA.pdf")]
        [InlineData(@"C:\Users\USER\OneDrive\Desktop\Allfiles\img20250405_10144740.pdf")]
        public async Task DumpHeaderZoneAndPeriod(string pdfPath)
        {
            Assert.True(File.Exists(pdfPath), $"Introuvable: {pdfPath}");
            var ocr = await GetOcrCachedAsync(pdfPath);

            var lines = _fixture.AnalysisEngine.GroupWordsIntoLines(ocr.Words);
            var rows = _fixture.AnalysisEngine.BuildTable(lines);

            var extractorType = typeof(GenericDocumentMetadataExtractor);
            var getHeaderZoneLines = extractorType.GetMethod("GetHeaderZoneLines", BindingFlags.NonPublic | BindingFlags.Static)!;
            var headerLines = (List<string>)getHeaderZoneLines.Invoke(null, new object?[] { rows, ocr.FullText })!;

            var extractor = new GenericDocumentMetadataExtractor();
            var metadata = extractor.Extract(ocr.FullText, rows);

            // Reproduit le repli DocumentProcessingService.DerivePeriodFromTransactionDates
            // (prive) via le VRAI document parse (classifier + BankDocumentParser), pour
            // verifier le comportement de bout en bout sans refaire l'OCR (deja en cache).
            var documentType = _fixture.Classifier.Classify(ocr.FullText);
            var parser = _fixture.ParserFactory.GetParser(documentType);
            object? document = parser?.Parse(ocr.FullText, lines);
            if ((metadata.Period == null || (string.IsNullOrWhiteSpace(metadata.Period.Start) && string.IsNullOrWhiteSpace(metadata.Period.End)))
                && document is BankDocument bankDoc)
            {
                var processingServiceType = typeof(Codeium_Security.Services.DocumentProcessingService);
                var deriveMethod = processingServiceType.GetMethod("DerivePeriodFromTransactionDates", BindingFlags.NonPublic | BindingFlags.Static)!;
                var derived = (ExtractionPeriod?)deriveMethod.Invoke(null, new object?[] { bankDoc });
                if (derived != null) metadata.Period = derived;
            }

            _output.WriteLine($"===== {Path.GetFileName(pdfPath)} =====");
            _output.WriteLine($"Period.Start='{metadata.Period?.Start}' Period.End='{metadata.Period?.End}'");
            _output.WriteLine($"CustomerName='{metadata.CustomerName}'");
            _output.WriteLine("--- headerLines (" + headerLines.Count + ") ---");
            for (int i = 0; i < headerLines.Count; i++)
                _output.WriteLine($"[{i}] '{headerLines[i]}'");
            _output.WriteLine("--- fullText (first 1500 chars) ---");
            _output.WriteLine((ocr.FullText ?? "").Substring(0, Math.Min(1500, (ocr.FullText ?? "").Length)));
        }
    }
}
