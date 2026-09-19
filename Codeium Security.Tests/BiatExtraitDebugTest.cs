using Codeium_Security.Models;
using Codeium_Security.Services;
using Xunit;
using Xunit.Abstractions;

namespace Codeium_Security.Tests
{
    // Test de diagnostic temporaire (harnais uniquement, aucune logique de parsing ici) pour
    // executer le pipeline reel sur les 3 fichiers demandes et rapporter le resultat.
    [Collection("Pipeline")]
    public class BiatExtraitDebugTest
    {
        private readonly PipelineFixture _fixture;
        private readonly ITestOutputHelper _output;

        public BiatExtraitDebugTest(PipelineFixture fixture, ITestOutputHelper output)
        {
            _fixture = fixture;
            _output = output;
        }

        [Theory]
        [InlineData(@"..\..\Images\Releve BH FAH DISTRIBUTION.pdf")]
        public async Task DebugAccountStatement(string fileName)
        {
            string pdfPath = Path.Combine(
                AppContext.BaseDirectory, "..", "..", "..", "..",
                "TrainingData", "SourceDocuments", fileName);
            pdfPath = Path.GetFullPath(pdfPath);
            Assert.True(File.Exists(pdfPath), $"Introuvable: {pdfPath}");

            var result = await _fixture.ProcessingService.ProcessFileAsync(pdfPath, Path.GetFileName(pdfPath));

            Console.WriteLine($"===== RESULT {fileName} =====");
            Console.WriteLine($"BankName='{(result.Document as BankDocument)?.BankName}'");
            if (result.Document is BankDocument bankDoc)
            {
                foreach (var acc in bankDoc.Accounts)
                {
                    Console.WriteLine($"AccountNumber='{acc.AccountNumber}' SoldeInitial={acc.SoldeInitial} SoldeFinal={acc.SoldeFinal} Tx={acc.Transactions.Count}");
                    int i = 0;
                    foreach (var tx in acc.Transactions)
                        Console.WriteLine($"[{i++}] {tx.Date} | D={tx.Debit} | C={tx.Credit} | {tx.Libelle}");
                }
            }
            else
            {
                Console.WriteLine($"Document non reconnu comme BankDocument : {result.Document?.GetType().Name ?? "null"}");
            }
        }
    }
}
