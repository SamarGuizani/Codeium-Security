using Codeium_Security.Models;
using Xunit;
using Xunit.Abstractions;

namespace Codeium_Security.Tests
{
    // Test de DIAGNOSTIC, pas de non-regression (meme esprit que NewBankExplorationTests) : fait
    // tourner le pipeline reel (OCR Tesseract inclus, aucun mock) sur TOUS les fichiers UBCI de
    // reference du dossier codeuim/UBCI et affiche le resultat pour verifier a l'oeil les 5
    // structures reellement rencontrees (extrait vs releve, scan degrade vs PDF natif). Ne fait
    // AUCUNE assertion sur le contenu - aucun snapshot verite-terrain n'existe encore pour UBCI.
    [Collection("Pipeline")]
    public class UbciExplorationTests
    {
        private readonly PipelineFixture _fixture;
        private readonly ITestOutputHelper _output;

        public UbciExplorationTests(PipelineFixture fixture, ITestOutputHelper output)
        {
            _fixture = fixture;
            _output = output;
        }

        private const string UbciDir = @"C:\Users\USER\OneDrive\Desktop\codeuim\UBCI";

        public static IEnumerable<object[]> TargetFiles()
        {
            string[] files =
            {
                "bq bidah 02-2025ubci.pdf",
                "dakhliubci.pdf",
                "DURAWOOD TUNISIENNE - MAI.pdf",
                "EXTRAIT DAKHLIubci.pdf",
                "ubci.pdf",
            };
            foreach (var f in files)
                yield return new object[] { f };
        }

        [Theory]
        [MemberData(nameof(TargetFiles))]
        public async Task Explore(string fileName)
        {
            string pdfPath = Path.Combine(UbciDir, fileName);
            if (!File.Exists(pdfPath))
            {
                _output.WriteLine($"[SKIP] Introuvable : {pdfPath}");
                return;
            }

            var result = await _fixture.ProcessingService.ProcessFileAsync(pdfPath, fileName);

            _output.WriteLine($"===== {fileName} =====");
            _output.WriteLine($"DetectedType: {result.DetectedType} | PageCount: {result.PageCount} | OcrConfidence: {result.OcrConfidence:F1} | NeedsReview: {result.NeedsReview}");

            _output.WriteLine($"----- DebugLines ({result.DebugLines.Count}) -----");
            for (int i = 0; i < result.DebugLines.Count; i++)
                _output.WriteLine($"L{i:D3}: {result.DebugLines[i]}");
            _output.WriteLine("----- FIN DebugLines -----");

            if (result.Document is BankDocument bankDoc)
            {
                _output.WriteLine($"BankName: '{bankDoc.BankName}' | Accounts: {bankDoc.Accounts.Count}");
                foreach (var acc in bankDoc.Accounts)
                {
                    _output.WriteLine($"  -- Compte '{acc.AccountNumber}' | Devise={acc.Currency} | SoldeInitial={acc.SoldeInitial} | SoldeFinal={acc.SoldeFinal} | Transactions={acc.Transactions.Count}");
                    int shown = 0;
                    foreach (var tx in acc.Transactions)
                    {
                        _output.WriteLine($"     [{shown}] {tx.Date} | {tx.Libelle} | D={tx.Debit} | C={tx.Credit}");
                        shown++;
                    }
                }
            }
            else
            {
                _output.WriteLine($"Document: {result.Document?.GetType().Name ?? "null"} (pas un BankDocument)");
            }

            Assert.True(true);
        }
    }
}
