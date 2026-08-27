using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Codeium_Security.Models;
using Codeium_Security.OCR;
using Xunit;
using Xunit.Abstractions;

namespace Codeium_Security.Tests
{
    // Test de diagnostic temporaire (harnais uniquement) : passe tous les PDF du dossier
    // Desktop\Rest dans le VRAI pipeline (OCR Tesseract inclus) et ecrit, pour chacun, un
    // resume (banque detectee, comptes, transactions, sommes) + les lignes "TOTAUX/Total"
    // trouvees en bas de chaque page OCR, pour reperer les ecarts avec le total imprime par
    // la banque. Met en cache le resultat OCR sur disque (couteux) pour pouvoir relancer
    // rapidement l'analyse apres un correctif de parsing.
    [Collection("Pipeline")]
    public class RestBatchDiagnosticTest
    {
        private readonly PipelineFixture _fixture;
        private readonly ITestOutputHelper _output;

        public RestBatchDiagnosticTest(PipelineFixture fixture, ITestOutputHelper output)
        {
            _fixture = fixture;
            _output = output;
        }

        private const string RestDir = @"C:\Users\USER\OneDrive\Desktop\Rest";

        private static readonly string LogDir = Path.Combine(
            Environment.GetEnvironmentVariable("TEMP") ?? Path.GetTempPath(), "rest_batch_diag");

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

        public static IEnumerable<object[]> RestFiles()
        {
            if (!Directory.Exists(RestDir)) yield break;
            foreach (var f in Directory.EnumerateFiles(RestDir, "*.pdf").OrderBy(f => f))
                yield return new object[] { f };
        }

        private static readonly Regex TotauxLineRegex = new(
            @"(TOTAUX?|TOTAL\s+DES\s+MOUVEMENTS|NOUVEAU\s+SOLDE|SOLDE\s+FINAL|SOLDE\s+DE\s+CLOTURE|SOLDE\s+ACTUEL)\D{0,40}(-?\d{1,3}(?:[ .,]?\d{3})*[.,]\d{2,3})(?:\D{1,20}(-?\d{1,3}(?:[ .,]?\d{3})*[.,]\d{2,3}))?",
            RegexOptions.IgnoreCase);

        [Theory]
        [MemberData(nameof(RestFiles))]
        public async Task DiagnoseFile(string pdfPath)
        {
            Assert.True(File.Exists(pdfPath), $"Introuvable: {pdfPath}");
            Directory.CreateDirectory(LogDir);
            string logPath = Path.Combine(LogDir, SafeName(pdfPath) + ".log");
            var log = new List<string>();
            void L(string s) => log.Add(s);

            OcrResult? ocr = null;
            object? document = null;
            string documentType = "";
            Exception? caught = null;

            try
            {
                ocr = await GetOcrCachedAsync(pdfPath);

                var lines = _fixture.AnalysisEngine.GroupWordsIntoLines(ocr.Words);
                var docType = _fixture.Classifier.Classify(ocr.FullText);
                documentType = docType.ToString();
                var parser = _fixture.ParserFactory.GetParser(docType);
                document = parser?.Parse(ocr.FullText, lines);
            }
            catch (Exception ex)
            {
                caught = ex;
            }

            L($"===== {Path.GetFileName(pdfPath)} =====");
            if (caught != null)
            {
                L($"EXCEPTION: {caught.GetType().Name}: {caught.Message}");
                File.WriteAllLines(logPath, log);
                _output.WriteLine($"[{Path.GetFileName(pdfPath)}] EXCEPTION - voir {logPath}");
                return;
            }

            L($"PageCount={ocr!.PageCount} Confidence={ocr.Confidence}");
            L($"DetectedType={documentType}");

            if (document is BankDocument bankDoc)
            {
                var sumCalc = new Codeium_Security.Services.Calculation.TransactionSumCalculator();
                L($"BankName='{bankDoc.BankName}' Accounts.Count={bankDoc.Accounts.Count}");
                foreach (var acc in bankDoc.Accounts)
                {
                    var sums = sumCalc.Calculate(acc);
                    L($"AccountNumber='{acc.AccountNumber}' SoldeInitial={acc.SoldeInitial} SoldeFinal={acc.SoldeFinal} Tx={acc.Transactions.Count} | SUM(Debit)={sums.TotalDebit} SUM(Credit)={sums.TotalCredit} | ParsedTotalDebit={acc.TotalDebit} ParsedTotalCredit={acc.TotalCredit}")
                        ;

                    // Signale les montants individuels demesures (> 1 million) pour reperer un
                    // outlier responsable d'une somme totale absurde, sans avoir a tout dumper.
                    foreach (var tx in acc.Transactions)
                    {
                        if ((tx.Debit.HasValue && tx.Debit.Value > 1_000_000m) || (tx.Credit.HasValue && tx.Credit.Value > 1_000_000m))
                            L($"    >>> OUTLIER: {tx.Date} | D={tx.Debit} | C={tx.Credit} | {tx.Libelle}");
                    }

                    // Petit compte (< 60 tx) : dump complet pour comparer manuellement ligne a ligne.
                    if (acc.Transactions.Count < 600)
                    {
                        int ti = 0;
                        foreach (var tx in acc.Transactions)
                            L($"    [{ti++}] {tx.Date} | D={tx.Debit} | C={tx.Credit} | {tx.Libelle}");
                    }
                }
            }
            else
            {
                L($"Document non reconnu comme BankDocument : {document?.GetType().Name ?? "null"}");
            }

            L("----- Lignes TOTAUX/SOLDE trouvees dans l'OCR brut -----");
            for (int i = 0; i < ocr.PageTexts.Count; i++)
            {
                var t = ocr.PageTexts[i] ?? "";
                foreach (Match m in TotauxLineRegex.Matches(t))
                {
                    L($"Page {i + 1}: {m.Value.Replace("\n", " ").Trim()}");
                }
            }

            File.WriteAllLines(logPath, log);
            _output.WriteLine($"[{Path.GetFileName(pdfPath)}] Log ecrit dans {logPath}");
        }
    }
}
