using Codeium_Security.Models;
using Codeium_Security.Services;
using Xunit;
using Xunit.Abstractions;

namespace Codeium_Security.Tests
{
    // Test de diagnostic temporaire : fait tourner le vrai pipeline OCR sur "attijari banque.pdf"
    // et affiche le JSON reellement produit par le code, pour comparaison avec le resultat de
    // reference (lu manuellement dans le PDF) afin d'identifier les bugs du parser Attijari.
    [Collection("Pipeline")]
    public class AttijariDebugTest
    {
        private readonly PipelineFixture _fixture;
        private readonly ITestOutputHelper _output;

        public AttijariDebugTest(PipelineFixture fixture, ITestOutputHelper output)
        {
            _fixture = fixture;
            _output = output;
        }

        [Fact]
        public async Task DebugAccountStatement()
        {
            string pdfPath = Path.Combine(
                AppContext.BaseDirectory, "..", "..", "..", "..",
                "TrainingData", "SourceDocuments", "attijari banque.pdf");
            pdfPath = Path.GetFullPath(pdfPath);
            Assert.True(File.Exists(pdfPath), $"Introuvable: {pdfPath}");

            var sw = new StringWriter();
            var originalOut = Console.Out;
            Console.SetOut(sw);
            DocumentProcessingResult result;
            try
            {
                result = await _fixture.ProcessingService.ProcessFileAsync(pdfPath, Path.GetFileName(pdfPath));
            }
            finally
            {
                Console.SetOut(originalOut);
            }

            foreach (var line in sw.ToString().Split('\n'))
            {
                if (line.Contains("DIAG-CELLS") || line.Contains("BTE") || line.Contains("ATTIJARI"))
                    _output.WriteLine(line.TrimEnd('\r'));
            }

            _output.WriteLine("===== RESULT =====");
            _output.WriteLine($"BankName='{(result.Document as BankDocument)?.BankName}'");
            if (result.Document is BankDocument bankDoc)
            {
                foreach (var acc in bankDoc.Accounts)
                {
                    decimal sumDebit = 0, sumCredit = 0;
                    foreach (var t in acc.Transactions) { sumDebit += t.Debit ?? 0; sumCredit += t.Credit ?? 0; }
                    _output.WriteLine($"AccountNumber='{acc.AccountNumber}' Rib='{acc.Rib}' Currency='{acc.Currency}' SoldeInitial={acc.SoldeInitial} SoldeFinal={acc.SoldeFinal} TotalDebit={acc.TotalDebit} TotalCredit={acc.TotalCredit} Tx={acc.Transactions.Count} SumDebit={sumDebit} SumCredit={sumCredit}");
                    int i = 0;
                    foreach (var tx in acc.Transactions)
                        _output.WriteLine($"[{i++}] {tx.Date} | D={tx.Debit} | C={tx.Credit} | {tx.Libelle}");
                }
            }
        }
    }
}
