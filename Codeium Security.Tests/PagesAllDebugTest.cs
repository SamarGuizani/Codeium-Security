using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using ClosedXML.Excel;
using Codeium_Security.Models;
using Codeium_Security.OCR;
using Codeium_Security.Services.Export;
using Xunit;
using Xunit.Abstractions;

namespace Codeium_Security.Tests
{
    // Test de diagnostic temporaire (harnais uniquement) pour investiguer l'ecart signale par
    // l'utilisateur entre le releve BIAT imprime (TOTAUX Debit/Credit sur la derniere page) et
    // le total calcule par TransactionSumCalculator sur les transactions extraites. Met en cache
    // le resultat OCR (couteux, Tesseract) sur disque pour iterer rapidement sur le parsing seul.
    [Collection("Pipeline")]
    public class PagesAllDebugTest
    {
        private readonly PipelineFixture _fixture;
        private readonly ITestOutputHelper _output;

        public PagesAllDebugTest(PipelineFixture fixture, ITestOutputHelper output)
        {
            _fixture = fixture;
            _output = output;
        }

        private static readonly string LogDir = Path.Combine(
            Environment.GetEnvironmentVariable("TEMP") ?? Path.GetTempPath(), "pages_all_diag");

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
        [InlineData(@"C:\Users\USER\OneDrive\Desktop\Allfiles\Pages_All.pdf", "1069656")]
        [InlineData(@"C:\Users\USER\OneDrive\Desktop\Allfiles\Pages_All (1).pdf", "199163")]
        [InlineData(@"C:\Users\USER\OneDrive\Desktop\Allfiles\Pages_All.pdf BIAT.pdf", "")]
        [InlineData(@"C:\Users\USER\OneDrive\Desktop\Allfiles\EXTRAIT (1)biat.pdf", "1350000")]
        [InlineData(@"C:\Users\USER\OneDrive\Desktop\Allfiles\EXTRAIT BANCAIRE_BT.pdf", "")]
        public async Task DebugPagesAll(string pdfPath, string targetAmountDigits)
        {
            Assert.True(File.Exists(pdfPath), $"Introuvable: {pdfPath}");
            Directory.CreateDirectory(LogDir);
            string logPath = Path.Combine(LogDir, Path.GetFileNameWithoutExtension(pdfPath).Replace(" ", "_") + ".log");
            var log = new List<string>();
            void L(string s) { log.Add(s); }

            var ocr = await GetOcrCachedAsync(pdfPath);
            L($"===== OCR {pdfPath} =====");
            L($"PageCount={ocr.PageCount} PageTexts.Count={ocr.PageTexts.Count} Confidence={ocr.Confidence}");
            for (int i = 0; i < ocr.PageTexts.Count; i++)
            {
                var t = ocr.PageTexts[i] ?? "";
                bool hasTarget = !string.IsNullOrEmpty(targetAmountDigits) &&
                    Regex.IsMatch(t.Replace(" ", "").Replace(".", "").Replace(",", ""), Regex.Escape(targetAmountDigits));
                L($"--- Page {i + 1} length={t.Length} hasTarget={hasTarget}");
                if (hasTarget)
                    L($"    FULL TEXT PAGE {i + 1}:\n{t}");
            }

            var lines = _fixture.AnalysisEngine.GroupWordsIntoLines(ocr.Words);
            var documentType = _fixture.Classifier.Classify(ocr.FullText);
            var parser = _fixture.ParserFactory.GetParser(documentType);
            object? document = parser?.Parse(ocr.FullText, lines);
            var sumCalc = new Codeium_Security.Services.Calculation.TransactionSumCalculator();

            L($"===== RESULT {pdfPath} =====");
            L($"DetectedType={documentType}");
            if (document is BankDocument bankDoc)
            {
                L($"BankName='{bankDoc.BankName}' Accounts.Count={bankDoc.Accounts.Count}");
                int totalTx = 0;
                foreach (var acc in bankDoc.Accounts)
                {
                    totalTx += acc.Transactions.Count;
                    var sums = sumCalc.Calculate(acc);
                    L($"AccountNumber='{acc.AccountNumber}' SoldeInitial={acc.SoldeInitial} SoldeFinal={acc.SoldeFinal} Tx={acc.Transactions.Count} | SUM(Debit)={sums.TotalDebit} SUM(Credit)={sums.TotalCredit}");
                    int ti = 0;
                    foreach (var tx in acc.Transactions)
                        L($"    [{ti++}] {tx.Date} | D={tx.Debit} | C={tx.Credit} | {tx.Libelle}");
                    if (!string.IsNullOrEmpty(targetAmountDigits))
                    {
                        var matches = acc.Transactions.Where(t =>
                            (t.Debit?.ToString(CultureInfo.InvariantCulture).Replace(".", "") ?? "").Contains(targetAmountDigits) ||
                            (t.Credit?.ToString(CultureInfo.InvariantCulture).Replace(".", "") ?? "").Contains(targetAmountDigits)).ToList();
                        foreach (var m in matches)
                            L($"    >>> MATCH TARGET AMOUNT AS SINGLE TX: {m.Date} | D={m.Debit} | C={m.Credit} | {m.Libelle}");

                        string sumDebitDigits = sums.TotalDebit.ToString(CultureInfo.InvariantCulture).Replace(".", "");
                        string sumCreditDigits = sums.TotalCredit.ToString(CultureInfo.InvariantCulture).Replace(".", "");
                        if (sumDebitDigits.Contains(targetAmountDigits) || sumCreditDigits.Contains(targetAmountDigits))
                            L($"    >>> MATCH TARGET AMOUNT IN SUM(Debit/Credit) FOR THIS ACCOUNT");
                    }
                }
                L($"TOTAL TRANSACTIONS ACROSS ACCOUNTS: {totalTx}");

                var exporter = new BankExcelExporter();
                byte[] xlsxBytes = exporter.Export(bankDoc);
                using var wb = new XLWorkbook(new MemoryStream(xlsxBytes));
                L("===== EXCEL OUTPUT (Total Debit/Credit per sheet) =====");
                foreach (var ws in wb.Worksheets)
                {
                    string totalDebitCell = ws.Cell(1, 2).GetValue<string>();
                    string totalCreditCell = ws.Cell(2, 2).GetValue<string>();
                    L($"Sheet '{ws.Name}': Total Debit={totalDebitCell} Total Credit={totalCreditCell}");
                }
            }
            else
            {
                L($"Document non reconnu comme BankDocument : {document?.GetType().Name ?? "null"}");
            }

            File.WriteAllLines(logPath, log);
            _output.WriteLine($"Log ecrit dans {logPath} ({log.Count} lignes)");
        }
    }
}
