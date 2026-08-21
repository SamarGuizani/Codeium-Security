using Codeium_Security.Models;
using Codeium_Security.Services;
using Xunit;
using Xunit.Abstractions;

namespace Codeium_Security.Tests
{
    // Test de diagnostic temporaire : fait tourner le vrai pipeline OCR sur les 4 formes BTK
    // connues (BANK-TNDbtk.pdf / BANK-TND (1)btk.pdf = export web BTK@DIRECT, Extrait BTK
    // Janvier-2024.pdf = "EXTRAIT DE COMPTE" classique, BTK Dahlia 2024.pdf = "RELEVE DE COMPTE"
    // annuel 112 pages avec "Report du ..." / "Total des operations" par page) pour verifier que
    // le parser BTK gere correctement les 3 formats sans erreur de Debit/Credit.
    [Collection("Pipeline")]
    public class BtkDebugTest
    {
        private readonly PipelineFixture _fixture;
        private readonly ITestOutputHelper _output;

        public BtkDebugTest(PipelineFixture fixture, ITestOutputHelper output)
        {
            _fixture = fixture;
            _output = output;
        }

        public static IEnumerable<object[]> Files()
        {
            yield return new object[] { "BANK-TNDbtk.pdf" };
            yield return new object[] { "BANK-TND (1)btk.pdf" };
            yield return new object[] { "Extrait BTK Janvier-2024.pdf" };
            yield return new object[] { "BTK Dahlia 2024.pdf" };
        }

        [Theory]
        [MemberData(nameof(Files))]
        public async Task DebugAccountStatement(string fileName)
        {
            string pdfPath = Path.Combine(
                AppContext.BaseDirectory, "..", "..", "..", "..",
                "TrainingData", "SourceDocuments", fileName);
            pdfPath = Path.GetFullPath(pdfPath);
            Assert.True(File.Exists(pdfPath), $"Introuvable: {pdfPath}");

            var sw = new StringWriter();
            var originalOut = Console.Out;
            Console.SetOut(sw);
            DocumentProcessingResult result;
            try
            {
                result = await _fixture.ProcessingService.ProcessFileAsync(pdfPath, fileName);
            }
            finally
            {
                Console.SetOut(originalOut);
            }

            foreach (var line in sw.ToString().Split('\n'))
            {
                if (line.Contains("BTE-WARN") || line.Contains("BTE-OK") || line.Contains("BTK-ROWS") || line.Contains("BTK-DEBUG") || line.Contains("DIAG-CELLS"))
                    _output.WriteLine(line.TrimEnd('\r'));
            }

            _output.WriteLine($"===== RESULT {fileName} =====");
            _output.WriteLine($"BankName='{(result.Document as BankDocument)?.BankName}'");
            if (result.Document is BankDocument bankDoc)
            {
                foreach (var acc in bankDoc.Accounts)
                {
                    decimal sumDebit = 0, sumCredit = 0;
                    foreach (var t in acc.Transactions) { sumDebit += t.Debit ?? 0; sumCredit += t.Credit ?? 0; }
                    _output.WriteLine($"AccountNumber='{acc.AccountNumber}' Rib='{acc.Rib}' SoldeInitial={acc.SoldeInitial} SoldeFinal={acc.SoldeFinal} TotalDebit={acc.TotalDebit} TotalCredit={acc.TotalCredit} Tx={acc.Transactions.Count} SumDebit={sumDebit} SumCredit={sumCredit}");
                    // N'imprime que les 30 premieres et 30 dernieres transactions pour rester lisible sur 112 pages
                    int n = acc.Transactions.Count;
                    for (int i = 0; i < n; i++)
                    {
                        if (n > 80 && i == 30) { _output.WriteLine("     ... (milieu omis) ..."); i = n - 30; }
                        var tx = acc.Transactions[i];
                        _output.WriteLine($"[{i}] {tx.Date} | D={tx.Debit} | C={tx.Credit} | {tx.Libelle}");
                    }
                }
            }
        }
    }
}
