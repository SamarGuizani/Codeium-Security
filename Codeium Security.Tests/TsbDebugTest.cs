using Codeium_Security.Models;
using Codeium_Security.Services;
using Xunit;
using Xunit.Abstractions;

namespace Codeium_Security.Tests
{
    // Test de diagnostic temporaire pour investiguer le bug TSB signale par l'utilisateur.
    [Collection("Pipeline")]
    public class TsbDebugTest
    {
        private readonly PipelineFixture _fixture;
        private readonly ITestOutputHelper _output;

        public TsbDebugTest(PipelineFixture fixture, ITestOutputHelper output)
        {
            _fixture = fixture;
            _output = output;
        }

        public static IEnumerable<object[]> Files()
        {
            yield return new object[] { "EXTRAIT TSB.pdf" };
            yield return new object[] { "extrait PSP 2025.pdf" };
            yield return new object[] { "TSB_relevé.pdf" };
        }

        [Theory]
        [MemberData(nameof(Files))]
        public async Task DebugAccountStatement(string fileName)
        {
            string pdfPath = Path.Combine(RepoPaths.SourceDocumentsDir, fileName);
            Assert.True(File.Exists(pdfPath), $"Introuvable: {pdfPath}");

            var result = await _fixture.ProcessingService.ProcessFileAsync(pdfPath, fileName);

            _output.WriteLine($"===== RESULT {fileName} =====");
            _output.WriteLine($"DetectedType={result.Document?.GetType().Name ?? "null"}");
            _output.WriteLine($"BankName='{(result.Document as BankDocument)?.BankName}'");
            if (result.Document is BankDocument bankDoc)
            {
                foreach (var acc in bankDoc.Accounts)
                {
                    decimal sumDebit = 0, sumCredit = 0;
                    foreach (var t in acc.Transactions) { sumDebit += t.Debit ?? 0; sumCredit += t.Credit ?? 0; }
                    _output.WriteLine($"AccountNumber='{acc.AccountNumber}' Rib='{acc.Rib}' Currency='{acc.Currency}' SoldeInitial={acc.SoldeInitial} SoldeFinal={acc.SoldeFinal} TotalDebit={acc.TotalDebit} TotalCredit={acc.TotalCredit} Tx={acc.Transactions.Count} SumDebit={sumDebit} SumCredit={sumCredit}");
                    int n = acc.Transactions.Count;
                    for (int i = 0; i < n; i++)
                    {
                        var tx = acc.Transactions[i];
                        _output.WriteLine($"[{i}] {tx.Date} | D={tx.Debit} | C={tx.Credit} | {tx.Libelle}");
                    }
                }
            }
        }
    }
}
