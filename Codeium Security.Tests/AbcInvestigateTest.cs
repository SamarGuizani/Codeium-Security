using Codeium_Security.Models;
using Xunit;
using Xunit.Abstractions;

namespace Codeium_Security.Tests
{
    [Collection("Pipeline")]
    public class AbcInvestigateTest
    {
        private readonly PipelineFixture _fixture;
        private readonly ITestOutputHelper _output;

        public AbcInvestigateTest(PipelineFixture fixture, ITestOutputHelper output)
        {
            _fixture = fixture;
            _output = output;
        }

        [Fact]
        public async Task DebugAccountStatement()
        {
            string pdfPath = "C:\\Users\\USER\\OneDrive\\Desktop\\testing\\RELEVE ABC 102025.pdf";
            Assert.True(File.Exists(pdfPath), $"Introuvable: {pdfPath}");

            var result = await _fixture.ProcessingService.ProcessFileAsync(pdfPath, "RELEVE ABC 102025.pdf");

            _output.WriteLine($"BankName='{(result.Document as BankDocument)?.BankName}'");
            if (result.Document is BankDocument bankDoc)
            {
                foreach (var acc in bankDoc.Accounts)
                {
                    _output.WriteLine($"AccountNumber='{acc.AccountNumber}' Currency='{acc.Currency}' SoldeInitial={acc.SoldeInitial} SoldeFinal={acc.SoldeFinal} Tx={acc.Transactions.Count}");
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
