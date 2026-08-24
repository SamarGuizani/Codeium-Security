using Codeium_Security.Models;
using Xunit;
using Xunit.Abstractions;

namespace Codeium_Security.Tests
{
    [Collection("Pipeline")]
    public class BtkInvestigateTest2
    {
        private readonly PipelineFixture _fixture;
        private readonly ITestOutputHelper _output;

        public BtkInvestigateTest2(PipelineFixture fixture, ITestOutputHelper output)
        {
            _fixture = fixture;
            _output = output;
        }

        public static IEnumerable<object[]> Files()
        {
            yield return new object[] { "BANK-USD (1).pdf" };
            yield return new object[] { "BANK-USD.pdf" };
            yield return new object[] { "Zouheir mobil BTK.pdf" };
        }

        [Theory]
        [MemberData(nameof(Files))]
        public async Task DebugAccountStatement(string fileName)
        {
            string pdfPath = System.IO.Path.Combine(RepoPaths.SourceDocumentsDir, "btk", fileName);
            Assert.True(File.Exists(pdfPath), $"Introuvable: {pdfPath}");

            var result = await _fixture.ProcessingService.ProcessFileAsync(pdfPath, fileName);

            _output.WriteLine($"===== RESULT {fileName} =====");
            _output.WriteLine($"BankName='{(result.Document as BankDocument)?.BankName}'");
            if (result.Document is BankDocument bankDoc)
            {
                foreach (var acc in bankDoc.Accounts)
                {
                    _output.WriteLine($"AccountNumber='{acc.AccountNumber}' Currency='{acc.Currency}' SoldeInitial={acc.SoldeInitial} SoldeFinal={acc.SoldeFinal} TotalDebit={acc.TotalDebit} TotalCredit={acc.TotalCredit} Tx={acc.Transactions.Count}");
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
