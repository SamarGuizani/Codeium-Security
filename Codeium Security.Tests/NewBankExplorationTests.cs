using Codeium_Security.Models;
using Xunit;
using Xunit.Abstractions;

namespace Codeium_Security.Tests
{
    // Test de DIAGNOSTIC, pas de non-regression : fait tourner le pipeline reel sur des PDF des
    // nouvelles banques cibles (pas encore de snapshot/verite terrain) et affiche le resultat
    // pour evaluer a l'oeil l'effet des changements generiques. Ne fait AUCUNE assertion sur le
    // contenu - sert uniquement a observer. A supprimer une fois l'evaluation terminee.
    [Collection("Pipeline")]
    public class NewBankExplorationTests
    {
        private readonly PipelineFixture _fixture;
        private readonly ITestOutputHelper _output;

        public NewBankExplorationTests(PipelineFixture fixture, ITestOutputHelper output)
        {
            _fixture = fixture;
            _output = output;
        }

        private const string CodeuimDir = @"C:\Users\USER\OneDrive\Desktop\codeuim";

        public static IEnumerable<object[]> TargetFiles()
        {
            string[] files =
            {
                @"ATB\EXTRAIT ATB.pdf",
                @"ATB\atb.pdf",
                @"ATB\bq 02ATB.pdf",
                @"ATB\bq 02.pdf",
                @"ATB\BQ 12 ZIKO EMBATB.pdf",
                @"Qnb\EXTRAIT QNB 2024.pdf",
                @"Biat\Relevé Biat Mai 2026type3.pdf",
                @"Biat\EXTRAIT (1).pdf",
                @"Biat\export.pdf",
                @"Biat\extraitbiat (1).pdf",
                @"ABC\RLV ABC.pdf",
                @"ABC\abcbank.pdf",
                @"El Baraka\ALBARAKA_EXTRAIT.pdf",
                @"BNA\BNA  7-2025.pdf",
                @"BH\extrait BH.pdf",
                @"zitouna\extraitzitouna.pdf",
                @"zitouna\Relevé Zitouna 06.pdf",
                @"WIFAK\EXTRAIT WIFAK.pdf",
                @"WIFAK\wifak bank.pdf",
                @"Amen\AMEN.pdf",
                @"Amen\AMEN BQList.pdf",
                @"BTK\BANK-TND (1)btk.pdf",
                @"BTK\BANK-TNDbtk.pdf",
                @"BTK\BTK Dahlia 2024.pdf",
                @"BTK\Extrait BTK Janvier-2024.pdf",
            };
            foreach (var f in files)
                yield return new object[] { f };
        }

        [Theory]
        [MemberData(nameof(TargetFiles))]
        public async Task Explore(string relativePath)
        {
            string pdfPath = Path.Combine(CodeuimDir, relativePath);
            if (!File.Exists(pdfPath))
            {
                _output.WriteLine($"[SKIP] Introuvable : {pdfPath}");
                return;
            }

            var result = await _fixture.ProcessingService.ProcessFileAsync(pdfPath, Path.GetFileName(pdfPath));

            _output.WriteLine($"===== {relativePath} =====");
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
                        if (false && shown >= 5 && shown < acc.Transactions.Count - 3) { shown++; continue; }
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
