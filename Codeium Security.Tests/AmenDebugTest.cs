using Codeium_Security.Models;
using Xunit;
using Xunit.Abstractions;

namespace Codeium_Security.Tests
{
    [Collection("Pipeline")]
    public class AmenDebugTest
    {
        private readonly PipelineFixture _fixture;
        private readonly ITestOutputHelper _output;

        public AmenDebugTest(PipelineFixture fixture, ITestOutputHelper output)
        {
            _fixture = fixture;
            _output = output;
        }

        [Fact]
        public async Task DebugCurrency()
        {
            string pdfPath = "C:\\Users\\USER\\OneDrive\\Desktop\\testing\\AMEN BQList.pdf";
            Assert.True(File.Exists(pdfPath), $"Introuvable: {pdfPath}");

            var result = await _fixture.ProcessingService.ProcessFileAsync(pdfPath, "AMEN BQList.pdf");

            _output.WriteLine($"BankName='{(result.Document as BankDocument)?.BankName}'");
            if (result.Document is BankDocument bankDoc)
            {
                foreach (var acc in bankDoc.Accounts)
                {
                    _output.WriteLine($"AccountNumber='{acc.AccountNumber}' Currency='{acc.Currency}'");
                }
            }
        }
    }
}
